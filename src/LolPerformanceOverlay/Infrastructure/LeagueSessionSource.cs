using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.Infrastructure;

public sealed class LeagueSessionSource : ILeagueSessionSource
{
    private const int MaximumLocalJsonBytes = 2 * 1024 * 1024;
    private readonly IStaticGameDataProvider _staticData;
    private readonly object _lcuGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, string> _summonerNames = new(StringComparer.Ordinal);
    private readonly HttpClient _liveClient;
    private HttpClient? _lcuClient;
    private string? _platformRegion;
    private DateTimeOffset _nextRegionLookupAt;
    private string? _activeRiotId;
    private DateTimeOffset _nextActiveIdentityLookupAt;
    private int _queueId;
    private DateTimeOffset _nextMatchMetadataLookupAt;
    private int _activeWatchers;
    private bool _lifetimeCancellationCompleted;
    private bool _lifetimeDisposed;
    private volatile bool _disposed;

    public LeagueSessionSource(IStaticGameDataProvider staticData)
    {
        _staticData = staticData;
        _liveClient = CreateLoopbackClient();
        _liveClient.BaseAddress = new Uri("https://127.0.0.1:2999/");
        _liveClient.Timeout = TimeSpan.FromSeconds(1.5);
    }

    public async IAsyncEnumerable<LeagueSessionFrame> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryStartWatcher(out var lifetimeToken))
        {
            yield break;
        }

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetimeToken);
            var watchToken = linkedCancellation.Token;
            LeaguePhase lastKnownPhase = LeaguePhase.None;
            while (!watchToken.IsCancellationRequested && !_disposed)
            {
                LeagueSessionFrame frame;
                try
                {
                    if (!EnsureLcuClient())
                    {
                        frame = EmptyFrame(LeaguePhase.None, "等待 League Client");
                    }
                    else
                    {
                        await EnsurePlatformRegionAsync(watchToken);
                        var phaseText = await GetStringAsync(_lcuClient!, "lol-gameflow/v1/gameflow-phase", watchToken);
                        var phase = MapPhase(TrimJsonString(phaseText));
                        if ((lastKnownPhase == LeaguePhase.ChampSelect && phase != LeaguePhase.ChampSelect) ||
                            (lastKnownPhase is LeaguePhase.Loading or LeaguePhase.InGame &&
                             phase is not (LeaguePhase.Loading or LeaguePhase.InGame)))
                        {
                            ClearSessionState();
                        }

                        lastKnownPhase = phase;
                        frame = phase switch
                        {
                            LeaguePhase.ChampSelect => await ReadChampSelectAsync(watchToken),
                            LeaguePhase.InGame or LeaguePhase.Loading =>
                                await ReadLiveGameAsync(phase, watchToken),
                            LeaguePhase.EndOfGame => EmptyFrame(LeaguePhase.EndOfGame, "對局已結束"),
                            _ => EmptyFrame(phase, PhaseMessage(phase))
                        };
                    }
                }
                catch (OperationCanceledException) when (watchToken.IsCancellationRequested)
                {
                    yield break;
                }
                catch
                {
                    if (_disposed)
                    {
                        yield break;
                    }

                    ResetLcuConnection();
                    frame = EmptyFrame(lastKnownPhase, "本機資料暫時不可用，正在重連");
                }

                if (_disposed || watchToken.IsCancellationRequested)
                {
                    yield break;
                }

                yield return frame;
                try
                {
                    var delay = frame.Phase == LeaguePhase.None
                        ? TimeSpan.FromSeconds(2)
                        : TimeSpan.FromSeconds(1);
                    await Task.Delay(delay, watchToken);
                }
                catch (OperationCanceledException) when (watchToken.IsCancellationRequested)
                {
                    yield break;
                }
            }
        }
        finally
        {
            FinishWatcher();
        }
    }

    public ValueTask DisposeAsync()
    {
        HttpClient? lcuClient;
        lock (_lcuGate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            lcuClient = _lcuClient;
            _lcuClient = null;
        }

        _lifetime.Cancel();
        lcuClient?.Dispose();
        _liveClient.Dispose();
        ClearSessionState();
        CompleteLifetimeCancellation();
        return ValueTask.CompletedTask;
    }

    private bool TryStartWatcher(out CancellationToken lifetimeToken)
    {
        lock (_lcuGate)
        {
            if (_disposed)
            {
                lifetimeToken = default;
                return false;
            }

            _activeWatchers++;
            lifetimeToken = _lifetime.Token;
            return true;
        }
    }

    private void FinishWatcher()
    {
        var disposeLifetime = false;
        lock (_lcuGate)
        {
            _activeWatchers--;
            if (_activeWatchers < 0)
            {
                throw new InvalidOperationException("League session watcher count became negative.");
            }

            if (_disposed && _lifetimeCancellationCompleted &&
                _activeWatchers == 0 && !_lifetimeDisposed)
            {
                _lifetimeDisposed = true;
                disposeLifetime = true;
            }
        }

        if (disposeLifetime)
        {
            _lifetime.Dispose();
        }
    }

    private void CompleteLifetimeCancellation()
    {
        var disposeLifetime = false;
        lock (_lcuGate)
        {
            _lifetimeCancellationCompleted = true;
            if (_activeWatchers == 0 && !_lifetimeDisposed)
            {
                _lifetimeDisposed = true;
                disposeLifetime = true;
            }
        }

        if (disposeLifetime)
        {
            _lifetime.Dispose();
        }
    }

    private bool EnsureLcuClient()
    {
        lock (_lcuGate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_lcuClient is not null)
            {
                return true;
            }

            var discovered = LeagueClientDiscovery.TryDiscover();
            ResetLcuConnection();
            if (discovered is null)
            {
                return false;
            }

            _lcuClient = CreateLoopbackClient();
            _lcuClient.BaseAddress = new Uri($"{discovered.Protocol}://127.0.0.1:{discovered.Port}/");
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{discovered.Password}"));
            _lcuClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            _lcuClient.Timeout = TimeSpan.FromSeconds(2);
            return true;
        }
    }

    private async Task<LeagueSessionFrame> ReadChampSelectAsync(CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(_lcuClient!, "lol-champ-select/v1/session", cancellationToken);
        var parsedMembers = LeagueSessionParser.ParseChampSelectMembers(json);
        var descriptors = new ChampionDescriptor[parsedMembers.Count];
        var iconRequests = new ValueTask<string?>[parsedMembers.Count];
        var identityRequests = new ValueTask<string?>[parsedMembers.Count];
        for (var index = 0; index < parsedMembers.Count; index++)
        {
            var member = parsedMembers[index];
            var champion = _staticData.ResolveChampion(string.Empty, member.ChampionId);
            descriptors[index] = champion;
            iconRequests[index] = _staticData.EnsureChampionIconAsync(champion, cancellationToken);
            identityRequests[index] = !member.IsAnonymous && !string.IsNullOrWhiteSpace(member.Puuid)
                ? ResolveRiotIdAsync(member.Puuid, cancellationToken)
                : ValueTask.FromResult<string?>(null);
        }

        var members = new ChampSelectMember[parsedMembers.Count];
        for (var index = 0; index < parsedMembers.Count; index++)
        {
            var member = parsedMembers[index];
            var champion = descriptors[index];
            members[index] = new ChampSelectMember(
                member.StableKey,
                await identityRequests[index],
                member.Team,
                member.ChampionId,
                member.ChampionId > 0 ? champion.Name : string.Empty,
                await iconRequests[index],
                member.IsAnonymous);
        }

        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(_activeRiotId) && now >= _nextActiveIdentityLookupAt)
        {
            _nextActiveIdentityLookupAt = now.AddSeconds(10);
            _activeRiotId = await ReadCurrentSummonerRiotIdAsync(cancellationToken);
        }
        return new LeagueSessionFrame(
            LeaguePhase.ChampSelect,
            DateTimeOffset.Now,
            0,
            string.Empty,
            0,
            _activeRiotId,
            members,
            Array.Empty<RawPlayerState>(),
            PlatformRegion: _platformRegion);
    }

    private async Task<LeagueSessionFrame> ReadLiveGameAsync(
        LeaguePhase requestedPhase,
        CancellationToken cancellationToken)
    {
        string playerJson;
        string statsJson;
        try
        {
            var playerTask = GetStringAsync(_liveClient, "liveclientdata/playerlist", cancellationToken);
            var statsTask = GetStringAsync(_liveClient, "liveclientdata/gamestats", cancellationToken);
            await Task.WhenAll(playerTask, statsTask);
            playerJson = await playerTask;
            statsJson = await statsTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return EmptyFrame(requestedPhase, "等待遊戲內資料端點");
        }

        var parsedPlayers = LeagueSessionParser.ParseLivePlayers(playerJson);
        var stats = LeagueSessionParser.ParseGameStats(statsJson);
        var descriptors = new ChampionDescriptor[parsedPlayers.Count];
        var iconRequests = new ValueTask<string?>[parsedPlayers.Count];
        for (var index = 0; index < parsedPlayers.Count; index++)
        {
            var player = parsedPlayers[index];
            var champion = _staticData.ResolveChampion(player.ChampionName, player.ChampionId);
            descriptors[index] = champion;
            iconRequests[index] = _staticData.EnsureChampionIconAsync(champion, cancellationToken);
        }

        var players = new RawPlayerState[parsedPlayers.Count];
        for (var index = 0; index < parsedPlayers.Count; index++)
        {
            var player = parsedPlayers[index];
            var champion = descriptors[index];
            var items = new RawItemState[player.Items.Count];
            for (var itemIndex = 0; itemIndex < player.Items.Count; itemIndex++)
            {
                var item = player.Items[itemIndex];
                var staticGold = _staticData.GetItemGoldValue(item.ItemId);
                items[itemIndex] = new RawItemState(
                    item.ItemId,
                    item.Count,
                    staticGold > 0 ? staticGold : item.ReportedPrice);
            }

            players[index] = new RawPlayerState(
                $"{player.Team}:{player.RiotId}",
                player.RiotId,
                player.Team,
                champion.Key,
                champion.Name,
                await iconRequests[index],
                champion.Archetypes,
                player.Kills,
                player.Deaths,
                player.Assists,
                player.CreepScore,
                player.Level,
                items);
        }

        var now = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(_activeRiotId) && now >= _nextActiveIdentityLookupAt)
        {
            _nextActiveIdentityLookupAt = now.AddSeconds(10);
            try
            {
                var activeJson = await GetStringAsync(_liveClient, "liveclientdata/activeplayer", cancellationToken);
                _activeRiotId = LeagueSessionParser.ParseActiveRiotId(activeJson);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _activeRiotId = await ReadCurrentSummonerRiotIdAsync(cancellationToken);
            }
        }

        if (_queueId == 0 && now >= _nextMatchMetadataLookupAt)
        {
            _nextMatchMetadataLookupAt = now.AddSeconds(10);
            try
            {
                var gameflowJson = await GetStringAsync(_lcuClient!, "lol-gameflow/v1/session", cancellationToken);
                _queueId = LeagueSessionParser.ParseQueueId(gameflowJson);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Queue ID only tunes confidence timing; the mode name remains a safe fallback.
            }
        }

        return new LeagueSessionFrame(
            players.Length > 0 ? LeaguePhase.InGame : requestedPhase,
            DateTimeOffset.Now,
            stats.GameTimeSeconds,
            stats.GameMode,
            _queueId,
            _activeRiotId,
            Array.Empty<ChampSelectMember>(),
            players,
            players.Length > 0 ? null : "等待完整玩家資料",
            _platformRegion);
    }

    private ValueTask<string?> ResolveRiotIdAsync(string puuid, CancellationToken cancellationToken)
    {
        if (_summonerNames.TryGetValue(puuid, out var cached))
        {
            return ValueTask.FromResult<string?>(cached.Length == 0 ? "已識別玩家" : cached);
        }

        return new ValueTask<string?>(FetchRiotIdAsync(puuid, cancellationToken));
    }

    private async Task<string?> FetchRiotIdAsync(string puuid, CancellationToken cancellationToken)
    {
        try
        {
            var json = await GetStringAsync(
                _lcuClient!,
                $"lol-summoner/v2/summoners/puuid/{Uri.EscapeDataString(puuid)}",
                cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var gameName = GetJsonString(root, "gameName") ??
                           GetJsonString(root, "displayName");
            var tagLine = GetJsonString(root, "tagLine");
            var riotId = string.IsNullOrWhiteSpace(tagLine) ? gameName : $"{gameName}#{tagLine}";
            if (!string.IsNullOrWhiteSpace(riotId))
            {
                _summonerNames[puuid] = riotId;
            }

            return riotId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _summonerNames[puuid] = string.Empty;
            return "已識別玩家";
        }
    }

    private async Task<string?> ReadCurrentSummonerRiotIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await GetStringAsync(_lcuClient!, "lol-summoner/v1/current-summoner", cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var gameName = GetJsonString(root, "gameName") ?? GetJsonString(root, "displayName");
            var tag = GetJsonString(root, "tagLine");
            return string.IsNullOrWhiteSpace(tag) ? gameName : $"{gameName}#{tag}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void ResetLcuConnection()
    {
        lock (_lcuGate)
        {
            _lcuClient?.Dispose();
            _lcuClient = null;
            _platformRegion = null;
            _nextRegionLookupAt = default;
            ClearSessionState();
        }
    }

    private async Task EnsurePlatformRegionAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_platformRegion is not null || _lcuClient is null || now < _nextRegionLookupAt)
        {
            return;
        }

        _nextRegionLookupAt = now.AddSeconds(10);
        try
        {
            var json = await GetStringAsync(_lcuClient, "riotclient/region-locale", cancellationToken);
            using var document = JsonDocument.Parse(json);
            _platformRegion = PlatformRegionMapper.TryMap(GetJsonString(document.RootElement, "region"));
            if (_platformRegion is null)
            {
                _nextRegionLookupAt = now.AddMinutes(5);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Region-dependent optional history/link features stay unavailable rather than guessing.
        }
    }

    private static HttpClient CreateLoopbackClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                NetworkDestinationPolicy.AllowsLoopbackCertificateBypass(request.RequestUri)
        };
        return new HttpClient(handler);
    }

    private static async Task<string> GetStringAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(client.Timeout);
        var requestToken = deadline.Token;
        var destination = new Uri(
            client.BaseAddress ?? throw new InvalidOperationException("HTTP client has no base address."),
            path);
        NetworkDestinationPolicy.RequireAllowed(destination, NetworkDestinationPurpose.RuntimeData);
        using var response = await client.GetAsync(
            destination,
            HttpCompletionOption.ResponseHeadersRead,
            requestToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumLocalJsonBytes)
        {
            throw new InvalidDataException($"Local response exceeds the {MaximumLocalJsonBytes}-byte limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
        return await BoundedStreamReader.ReadUtf8Async(
            stream,
            MaximumLocalJsonBytes,
            requestToken);
    }

    private static LeaguePhase MapPhase(string? value) => value switch
    {
        "Lobby" => LeaguePhase.Lobby,
        "Matchmaking" or "ReadyCheck" => LeaguePhase.Matchmaking,
        "ChampSelect" => LeaguePhase.ChampSelect,
        "GameStart" => LeaguePhase.Loading,
        "InProgress" or "Reconnect" => LeaguePhase.InGame,
        "WaitingForStats" or "PreEndOfGame" or "EndOfGame" => LeaguePhase.EndOfGame,
        _ => LeaguePhase.None
    };

    private static string PhaseMessage(LeaguePhase phase) => phase switch
    {
        LeaguePhase.Lobby => "已連線，等待排隊",
        LeaguePhase.Matchmaking => "配對中",
        LeaguePhase.Loading => "遊戲載入中",
        _ => "等待選角或遊戲"
    };

    private static string TrimJsonString(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(value) ?? value.Trim('"');
        }
        catch
        {
            return value.Trim().Trim('"');
        }
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static LeagueSessionFrame EmptyFrame(LeaguePhase phase, string status) =>
        new(
            phase,
            DateTimeOffset.Now,
            0,
            string.Empty,
            0,
            null,
            Array.Empty<ChampSelectMember>(),
            Array.Empty<RawPlayerState>(),
            status);

    private void ClearSessionState()
    {
        _summonerNames.Clear();
        _activeRiotId = null;
        _nextActiveIdentityLookupAt = default;
        _queueId = 0;
        _nextMatchMetadataLookupAt = default;
    }
}
