using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LolPerformanceOverlay.Core;

/// <summary>
/// Live adapter over Riot's official ACCOUNT-V1 and LEAGUE-V4 APIs. It resolves a revealed
/// Riot ID to a PUUID, then reads that player's ranked entries. LEAGUE-V4's by-puuid entries
/// endpoint already returns every queue's entry for that player in one response, so this
/// class picks the best one in preference order -- the queue actually being played, when it
/// has a ranked ladder of its own; then Solo; then Flex -- rather than requiring an exact
/// match. That makes a no-ladder queue like ARAM behave the same as any ranked one instead of
/// reporting nothing: see <see cref="FindPreferredEntry"/>. When the account resolves but has
/// no entry anywhere in that order, the result is an <c>Available</c> profile with a null
/// <see cref="HistoricalProfile.OfficialRank"/> -- an honest "unranked", not a failure --
/// because the account genuinely exists and was found; <c>NotFound</c>/<c>RecordNotFound</c>
/// is reserved for when ACCOUNT-V1 itself cannot resolve the account. It reports only the
/// official rank -- it never fetches match history, so every profile it produces has
/// <see cref="HistoricalProfile.PlayStyle"/> null. Caching, deduplication, concurrency limits,
/// and stale/fresh handling all live in <see cref="HistoricalProfileCoordinator"/>; this class
/// only translates one request into HTTP calls, and the fallback above never costs a second
/// one -- it is purely a choice among entries already present in the one LEAGUE-V4 response.
/// </summary>
public sealed class RiotHistoricalProfileTransport : IHistoricalProfileTransport, IDisposable
{
    private const string ApiKeyHeaderName = "X-Riot-Token";
    private const int MaximumResponseBytes = 64 * 1024;
    private const string SoloRiotQueueType = "RANKED_SOLO_5x5";
    private const string FlexRiotQueueType = "RANKED_FLEX_SR";
    private static readonly HistoricalProfileSource Source =
        new(HistoricalSourceKind.LiveBackend, "Riot 官方牌位");

    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly object _rateLimitGate = new();
    private DateTimeOffset _rateLimitedUntil = DateTimeOffset.MinValue;

    public RiotHistoricalProfileTransport(
        string apiKey,
        TimeProvider? timeProvider = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    public async Task<HistoricalProfileTransportResult> FetchAsync(
        RevealedPlayerIdentity player,
        HistoricalProfileQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(query);

        var accountRoute = PlatformRegionMapper.TryMapAccountRegionalRoute(player.Region);
        if (accountRoute is null)
        {
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Unavailable,
                HistoricalFailureReason.ProviderUnavailable);
        }

        bool stillRateLimited;
        lock (_rateLimitGate)
        {
            stillRateLimited = _timeProvider.GetUtcNow() < _rateLimitedUntil;
        }

        if (stillRateLimited)
        {
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.RateLimited,
                HistoricalFailureReason.RequestThrottled);
        }

        var account = await GetAsync<AccountDto>(
            new Uri($"https://{accountRoute}.api.riotgames.com/riot/account/v1/accounts/by-riot-id/{Uri.EscapeDataString(player.GameName)}/{Uri.EscapeDataString(player.TagLine)}"),
            cancellationToken).ConfigureAwait(false);
        if (!account.Succeeded)
        {
            return account.Failure;
        }

        if (string.IsNullOrWhiteSpace(account.Value!.Puuid))
        {
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Malformed,
                HistoricalFailureReason.InvalidResponse);
        }

        var entries = await GetAsync<LeagueEntryDto[]>(
            new Uri($"https://{player.Region}.api.riotgames.com/lol/league/v4/entries/by-puuid/{Uri.EscapeDataString(account.Value.Puuid)}"),
            cancellationToken).ConfigureAwait(false);
        if (!entries.Succeeded)
        {
            return entries.Failure;
        }

        var match = FindPreferredEntry(entries.Value!, query.Queue);
        if (match is null)
        {
            // The account resolved -- ACCOUNT-V1 found this exact player -- and LEAGUE-V4
            // answered with their entries, there just are none anywhere in the preference
            // order (this queue's own ladder when it has one, then Solo, then Flex). That is
            // "this player is unranked in Solo and Flex", a normal fact about the player, not
            // a failed lookup -- RecordNotFound/NotFound is reserved for when ACCOUNT-V1
            // itself cannot resolve the account (see the account.Succeeded check above; a 404
            // there is the only remaining path to it). Returning Available with a null
            // OfficialRank, rather than a failure, is what lets OfficialRankAttachment.Describe
            // render this as "未" / 尚未定位 instead of the generic failure marker -- the same
            // distinction issue #8 already drew between "unranked" and "something is wrong".
            // Sample count 0 and InsufficientSample confidence are honest, not filler: this is
            // a rank-only lookup with nothing behind it, the same shape as the resolved-rank
            // profile below, just without a rank to report.
            var unrankedProfile = new HistoricalProfile(
                query.Queue,
                null,
                0,
                _timeProvider.GetUtcNow(),
                HistoricalConfidence.InsufficientSample,
                Array.Empty<HistoricalChampionUsage>(),
                Array.Empty<HistoricalRoleUsage>(),
                null,
                Source);
            return HistoricalProfileTransportResult.WithProfile(HistoricalProfileAvailability.Available, unrankedProfile);
        }

        var (entry, entryQueue) = match.Value;
        if (string.IsNullOrWhiteSpace(entry.Tier) || string.IsNullOrWhiteSpace(entry.Rank))
        {
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Malformed,
                HistoricalFailureReason.InvalidResponse);
        }

        HistoricalProfile profile;
        try
        {
            profile = new HistoricalProfile(
                query.Queue,
                // Labelled with entryQueue -- the queue this entry actually came from -- not
                // query.Queue. They are the same object only when no fallback was needed;
                // otherwise entryQueue is Solo or Flex, and OfficialRankAttachment depends on
                // this to keep the tooltip and row honest about a cross-queue rank instead of
                // mislabelling it as belonging to the queue actually being played.
                new OfficialRank(entryQueue, entry.Tier, entry.Rank, entry.LeaguePoints),
                0,
                _timeProvider.GetUtcNow(),
                HistoricalConfidence.InsufficientSample,
                Array.Empty<HistoricalChampionUsage>(),
                Array.Empty<HistoricalRoleUsage>(),
                null,
                Source);
        }
        catch (ArgumentException)
        {
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Malformed,
                HistoricalFailureReason.InvalidResponse);
        }

        return HistoricalProfileTransportResult.WithProfile(HistoricalProfileAvailability.Available, profile);
    }

    private async Task<FetchResult<T>> GetAsync<T>(Uri destination, CancellationToken cancellationToken)
        where T : class
    {
        NetworkDestinationPolicy.RequireAllowed(destination, NetworkDestinationPurpose.RuntimeData);
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, destination);
            request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, _apiKey);
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Timeout,
                HistoricalFailureReason.RequestTimedOut));
        }
        catch (HttpRequestException)
        {
            return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Offline,
                HistoricalFailureReason.NetworkOffline));
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                lock (_rateLimitGate)
                {
                    _rateLimitedUntil = _timeProvider.GetUtcNow() + retryAfter;
                }

                return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.RateLimited,
                    HistoricalFailureReason.RequestThrottled));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.NotFound,
                    HistoricalFailureReason.RecordNotFound));
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The credential itself is the problem (missing/expired/revoked key), not an
                // absence of policy approval, but the model has no dedicated state for that yet
                // and the practical effect for the player -- no data shown -- is the same.
                return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.Unavailable,
                    HistoricalFailureReason.ProviderUnavailable));
            }

            if ((int)response.StatusCode >= 500)
            {
                return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.ServerError,
                    HistoricalFailureReason.UpstreamFailure));
            }

            if (!response.IsSuccessStatusCode)
            {
                return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.Malformed,
                    HistoricalFailureReason.InvalidResponse));
            }

            RequireContentLengthWithinLimit(response.Content.Headers.ContentLength);
            string json;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                json = await BoundedStreamReader.ReadUtf8Async(stream, MaximumResponseBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.Malformed,
                    HistoricalFailureReason.InvalidResponse));
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(json);
            }
            catch (JsonException)
            {
                return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.Malformed,
                    HistoricalFailureReason.InvalidResponse));
            }

            if (value is null)
            {
                return FetchResult<T>.Fail(HistoricalProfileTransportResult.Failure(
                    HistoricalProfileAvailability.Malformed,
                    HistoricalFailureReason.InvalidResponse));
            }

            return FetchResult<T>.Ok(value);
        }
    }

    private static void RequireContentLengthWithinLimit(long? contentLength)
    {
        if (contentLength > MaximumResponseBytes)
        {
            throw new InvalidOperationException("Riot API response exceeds the configured size limit.");
        }
    }

    private static string? MapQueueType(HistoricalQueue queue) => queue.QueueId switch
    {
        420 => SoloRiotQueueType,
        440 => FlexRiotQueueType,
        _ => null
    };

    /// <summary>
    /// Picks the best entry out of the single LEAGUE-V4 response already fetched -- it already
    /// contains every queue's entry for this player, so no second HTTP call is ever needed to
    /// widen the search. Tried in <see cref="PreferenceOrder"/>'s order and returned with the
    /// <see cref="HistoricalQueue"/> the winning entry actually belongs to, so the caller can
    /// label the resulting <see cref="OfficialRank"/> honestly instead of with whatever queue
    /// was originally queried.
    /// </summary>
    private static (LeagueEntryDto Entry, HistoricalQueue Queue)? FindPreferredEntry(
        LeagueEntryDto[] entries,
        HistoricalQueue currentQueue)
    {
        foreach (var (riotQueueType, queue) in PreferenceOrder(currentQueue))
        {
            var found = Array.Find(entries, candidate =>
                string.Equals(candidate.QueueType, riotQueueType, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return (found, queue);
            }
        }

        return null;
    }

    /// <summary>
    /// The queue actually being played first, when it has a ranked ladder of its own; then
    /// Solo; then Flex -- de-duplicated, so a Solo or Flex query never checks its own ladder
    /// twice. A queue with no ladder of its own (ARAM and the like) has no "current" entry to
    /// prefer, so the order simply starts at Solo. <paramref name="currentQueue"/> itself (not
    /// the canonical <see cref="HistoricalQueue.RankedSolo"/>/<see cref="HistoricalQueue.RankedFlex"/>
    /// singleton) is yielded for the current-queue slot, so a caller-supplied queue instance
    /// keeps its own <see cref="HistoricalQueue.DisplayName"/> when no fallback was needed.
    /// </summary>
    private static IEnumerable<(string RiotQueueType, HistoricalQueue Queue)> PreferenceOrder(
        HistoricalQueue currentQueue)
    {
        var currentRiotQueueType = MapQueueType(currentQueue);
        if (currentRiotQueueType is not null)
        {
            yield return (currentRiotQueueType, currentQueue);
        }

        if (!string.Equals(currentRiotQueueType, SoloRiotQueueType, StringComparison.OrdinalIgnoreCase))
        {
            yield return (SoloRiotQueueType, HistoricalQueue.RankedSolo);
        }

        if (!string.Equals(currentRiotQueueType, FlexRiotQueueType, StringComparison.OrdinalIgnoreCase))
        {
            yield return (FlexRiotQueueType, HistoricalQueue.RankedFlex);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private readonly record struct FetchResult<T>(bool Succeeded, T? Value, HistoricalProfileTransportResult Failure)
        where T : class
    {
        public static FetchResult<T> Ok(T value) => new(true, value, null!);
        public static FetchResult<T> Fail(HistoricalProfileTransportResult failure) => new(false, null, failure);
    }

    // Riot's wire format is camelCase; explicit names avoid depending on a global
    // case-insensitive deserializer setting that could also loosen matching elsewhere.
    private sealed class AccountDto
    {
        [JsonPropertyName("puuid")]
        public string? Puuid { get; set; }
    }

    private sealed class LeagueEntryDto
    {
        [JsonPropertyName("queueType")]
        public string? QueueType { get; set; }

        [JsonPropertyName("tier")]
        public string? Tier { get; set; }

        [JsonPropertyName("rank")]
        public string? Rank { get; set; }

        [JsonPropertyName("leaguePoints")]
        public int LeaguePoints { get; set; }
    }
}
