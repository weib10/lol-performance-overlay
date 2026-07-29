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
    private readonly IStaticGameDataProvider _staticData;
    private readonly ConcurrentDictionary<string, string> _summonerNames = new(StringComparer.Ordinal);
    private readonly HttpClient _liveClient;
    private HttpClient? _lcuClient;
    private LeagueClientCredentials? _credentials;

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
        LeaguePhase lastKnownPhase = LeaguePhase.None;
        while (!cancellationToken.IsCancellationRequested)
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
                    var phaseText = await GetStringAsync(_lcuClient!, "lol-gameflow/v1/gameflow-phase", cancellationToken);
                    var phase = MapPhase(TrimJsonString(phaseText));
                    lastKnownPhase = phase;
                    frame = phase switch
                    {
                        LeaguePhase.ChampSelect => await ReadChampSelectAsync(cancellationToken),
                        LeaguePhase.InGame or LeaguePhase.Loading =>
                            await ReadLiveGameAsync(phase, cancellationToken),
                        LeaguePhase.EndOfGame => EmptyFrame(LeaguePhase.EndOfGame, "對局已結束"),
                        _ => EmptyFrame(phase, PhaseMessage(phase))
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch
            {
                ResetLcuConnection();
                frame = EmptyFrame(lastKnownPhase, "本機資料暫時不可用，正在重連");
            }

            yield return frame;
            try
            {
                var delay = frame.Phase == LeaguePhase.None
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromSeconds(1);
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _lcuClient?.Dispose();
        _liveClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private bool EnsureLcuClient()
    {
        var discovered = LeagueClientDiscovery.TryDiscover();
        if (discovered is null)
        {
            ResetLcuConnection();
            return false;
        }

        if (_credentials == discovered && _lcuClient is not null)
        {
            return true;
        }

        ResetLcuConnection();
        _credentials = discovered;
        _lcuClient = CreateLoopbackClient();
        _lcuClient.BaseAddress = new Uri($"{discovered.Protocol}://127.0.0.1:{discovered.Port}/");
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{discovered.Password}"));
        _lcuClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _lcuClient.Timeout = TimeSpan.FromSeconds(2);
        _summonerNames.Clear();
        return true;
    }

    private async Task<LeagueSessionFrame> ReadChampSelectAsync(CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(_lcuClient!, "lol-champ-select/v1/session", cancellationToken);
        var parsedMembers = LeagueSessionParser.ParseChampSelectMembers(json);
        var members = await Task.WhenAll(parsedMembers.Select(async member =>
        {
            var champion = _staticData.ResolveChampion(string.Empty, member.ChampionId);
            var icon = await _staticData.EnsureChampionIconAsync(champion, cancellationToken);
            string? riotId = null;
            if (!member.IsAnonymous && !string.IsNullOrWhiteSpace(member.Puuid))
            {
                riotId = await ResolveRiotIdAsync(member.Puuid, cancellationToken);
            }

            return new ChampSelectMember(
                member.StableKey,
                riotId,
                member.Team,
                member.ChampionId,
                member.ChampionId > 0 ? champion.Name : string.Empty,
                icon,
                member.IsAnonymous);
        }));

        var activeRiotId = await ReadCurrentSummonerRiotIdAsync(cancellationToken);
        return new LeagueSessionFrame(
            LeaguePhase.ChampSelect,
            DateTimeOffset.Now,
            0,
            string.Empty,
            0,
            activeRiotId,
            members,
            Array.Empty<RawPlayerState>());
    }

    private async Task<LeagueSessionFrame> ReadLiveGameAsync(
        LeaguePhase requestedPhase,
        CancellationToken cancellationToken)
    {
        string playerJson;
        string statsJson;
        try
        {
            playerJson = await GetStringAsync(_liveClient, "liveclientdata/playerlist", cancellationToken);
            statsJson = await GetStringAsync(_liveClient, "liveclientdata/gamestats", cancellationToken);
        }
        catch
        {
            return EmptyFrame(requestedPhase, "等待遊戲內資料端點");
        }

        var parsedPlayers = LeagueSessionParser.ParseLivePlayers(playerJson);
        var stats = LeagueSessionParser.ParseGameStats(statsJson);
        var players = await Task.WhenAll(parsedPlayers.Select(async player =>
        {
            var champion = _staticData.ResolveChampion(player.ChampionName, player.ChampionId);
            var icon = await _staticData.EnsureChampionIconAsync(champion, cancellationToken);
            var items = player.Items.Select(item =>
            {
                var staticGold = _staticData.GetItemGoldValue(item.ItemId);
                return new RawItemState(item.ItemId, item.Count, staticGold > 0 ? staticGold : item.ReportedPrice);
            }).ToArray();

            return new RawPlayerState(
                $"{player.Team}:{player.RiotId}",
                player.RiotId,
                player.Team,
                champion.Key,
                champion.Name,
                icon,
                champion.Archetypes,
                player.Kills,
                player.Deaths,
                player.Assists,
                player.CreepScore,
                player.Level,
                items);
        }));

        string? activeRiotId = null;
        try
        {
            var activeJson = await GetStringAsync(_liveClient, "liveclientdata/activeplayer", cancellationToken);
            activeRiotId = LeagueSessionParser.ParseActiveRiotId(activeJson);
        }
        catch
        {
            activeRiotId = await ReadCurrentSummonerRiotIdAsync(cancellationToken);
        }

        var queueId = 0;
        try
        {
            var gameflowJson = await GetStringAsync(_lcuClient!, "lol-gameflow/v1/session", cancellationToken);
            queueId = LeagueSessionParser.ParseQueueId(gameflowJson);
        }
        catch
        {
            // Queue ID only tunes confidence timing; the mode name remains a safe fallback.
        }

        return new LeagueSessionFrame(
            players.Length > 0 ? LeaguePhase.InGame : requestedPhase,
            DateTimeOffset.Now,
            stats.GameTimeSeconds,
            stats.GameMode,
            queueId,
            activeRiotId,
            Array.Empty<ChampSelectMember>(),
            players,
            players.Length > 0 ? null : "等待完整玩家資料");
    }

    private async Task<string?> ResolveRiotIdAsync(string puuid, CancellationToken cancellationToken)
    {
        if (_summonerNames.TryGetValue(puuid, out var cached))
        {
            return cached;
        }

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
        catch
        {
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
        catch
        {
            return null;
        }
    }

    private void ResetLcuConnection()
    {
        _lcuClient?.Dispose();
        _lcuClient = null;
        _credentials = null;
    }

    private static HttpClient CreateLoopbackClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        return new HttpClient(handler);
    }

    private static async Task<string> GetStringAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
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
}
