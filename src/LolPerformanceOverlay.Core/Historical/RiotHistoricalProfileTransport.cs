using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LolPerformanceOverlay.Core;

/// <summary>
/// Live adapter over Riot's official ACCOUNT-V1 and LEAGUE-V4 APIs. It resolves a revealed
/// Riot ID to a PUUID, then reads that player's ranked entries for the requested queue. It
/// reports only the official rank -- it never fetches match history, so every profile it
/// produces has <see cref="HistoricalProfile.PlayStyle"/> null. Caching, deduplication,
/// concurrency limits, and stale/fresh handling all live in <see cref="HistoricalProfileCoordinator"/>;
/// this class only translates one request into HTTP calls.
/// </summary>
public sealed class RiotHistoricalProfileTransport : IHistoricalProfileTransport, IDisposable
{
    private const string ApiKeyHeaderName = "X-Riot-Token";
    private const int MaximumResponseBytes = 64 * 1024;
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

        var queueType = MapQueueType(query.Queue);
        if (queueType is null)
        {
            // No ranked ladder exists for this queue (e.g. ARAM). This is not "the record
            // was not found", and it is not "the provider is broken" either -- the ladder
            // concept simply does not apply here, so no HTTP call is made at all. Availability
            // stays Unavailable (that is still the honest availability -- there is truly
            // nothing to serve), but the reason says exactly why, so the presentation layer
            // (OfficialRankAttachment) does not have to guess or, worse, tell the player the
            // data source is broken when nothing is broken.
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.Unavailable,
                HistoricalFailureReason.NoRankedLadder);
        }

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

        var entry = Array.Find(entries.Value!, candidate =>
            string.Equals(candidate.QueueType, queueType, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            // A resolved account with no entry for this queue means the player is simply
            // unranked there -- a normal, common outcome, not a transport error.
            return HistoricalProfileTransportResult.Failure(
                HistoricalProfileAvailability.NotFound,
                HistoricalFailureReason.RecordNotFound);
        }

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
                new OfficialRank(query.Queue, entry.Tier, entry.Rank, entry.LeaguePoints),
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
        420 => "RANKED_SOLO_5x5",
        440 => "RANKED_FLEX_SR",
        _ => null
    };

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
