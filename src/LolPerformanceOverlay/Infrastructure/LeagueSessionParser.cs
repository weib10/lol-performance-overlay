using System.Text.Json;
using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.Infrastructure;

internal static class LeagueSessionParser
{
    public static IReadOnlyList<ParsedChampSelectMember> ParseChampSelectMembers(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<ParsedChampSelectMember>();
        ParseTeam(document.RootElement, "myTeam", result);
        ParseTeam(document.RootElement, "theirTeam", result);
        return result;
    }

    public static ParsedGameStats ParseGameStats(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new ParsedGameStats(
            ReadDouble(root, "gameTime"),
            ReadString(root, "gameMode") ?? string.Empty);
    }

    public static IReadOnlyList<ParsedLivePlayer> ParseLivePlayers(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ParsedLivePlayer>();
        }

        var result = new List<ParsedLivePlayer>();
        var index = 0;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var scores = TryGet(element, "scores");
            var items = new List<ParsedLiveItem>();
            var itemArray = TryGet(element, "items");
            if (itemArray is { ValueKind: JsonValueKind.Array })
            {
                foreach (var item in itemArray.Value.EnumerateArray())
                {
                    items.Add(new ParsedLiveItem(
                        ReadInt(item, "itemID", "itemId"),
                        Math.Max(ReadInt(item, "count"), 1),
                        ReadInt(item, "price")));
                }
            }

            var riotId = ReadString(element, "riotId");
            if (string.IsNullOrWhiteSpace(riotId))
            {
                var gameName = ReadString(element, "riotIdGameName") ?? ReadString(element, "summonerName");
                var tagLine = ReadString(element, "riotIdTagLine");
                riotId = string.IsNullOrWhiteSpace(tagLine) ? gameName : $"{gameName}#{tagLine}";
            }

            result.Add(new ParsedLivePlayer(
                riotId ?? $"未知玩家 {index + 1}",
                ParseTeam(ReadString(element, "team")),
                ReadString(element, "championName") ?? "Unknown",
                ReadInt(element, "championId"),
                scores.HasValue ? ReadInt(scores.Value, "kills") : 0,
                scores.HasValue ? ReadInt(scores.Value, "deaths") : 0,
                scores.HasValue ? ReadInt(scores.Value, "assists") : 0,
                scores.HasValue ? ReadInt(scores.Value, "creepScore") : 0,
                ReadInt(element, "level"),
                items));
            index++;
        }

        return result;
    }

    public static string? ParseActiveRiotId(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var riotId = ReadString(root, "riotId");
        if (!string.IsNullOrWhiteSpace(riotId))
        {
            return riotId;
        }

        var gameName = ReadString(root, "riotIdGameName") ?? ReadString(root, "summonerName");
        var tag = ReadString(root, "riotIdTagLine");
        return string.IsNullOrWhiteSpace(tag) ? gameName : $"{gameName}#{tag}";
    }

    public static int ParseQueueId(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (TryGet(root, "gameData") is not { } gameData ||
            TryGet(gameData, "queue") is not { } queue)
        {
            return 0;
        }

        return ReadInt(queue, "id", "queueId");
    }

    private static void ParseTeam(
        JsonElement root,
        string propertyName,
        ICollection<ParsedChampSelectMember> result)
    {
        if (TryGet(root, propertyName) is not { ValueKind: JsonValueKind.Array } team)
        {
            return;
        }

        foreach (var member in team.EnumerateArray())
        {
            var visibility = ReadString(member, "nameVisibilityType");
            var puuid = ReadString(member, "puuid");
            var hidden = string.Equals(visibility, "HIDDEN", StringComparison.OrdinalIgnoreCase) ||
                         string.IsNullOrWhiteSpace(puuid) ||
                         puuid == "00000000-0000-0000-0000-000000000000";
            var cellId = ReadInt(member, "cellId");
            var teamId = ReadInt(member, "team");
            if (teamId is 1 or 2)
            {
                teamId *= 100;
            }

            result.Add(new ParsedChampSelectMember(
                $"cell-{cellId}-{teamId}",
                hidden ? null : puuid,
                teamId,
                ReadInt(member, "championId") is var championId && championId > 0
                    ? championId
                    : ReadInt(member, "championPickIntent"),
                hidden));
        }
    }

    private static int ParseTeam(string? value) => value?.ToUpperInvariant() switch
    {
        "ORDER" => 100,
        "CHAOS" => 200,
        _ => 0
    };

    private static JsonElement? TryGet(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        TryGet(element, propertyName) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = TryGet(element, propertyName);
            if (value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt32(out var result))
            {
                return result;
            }
        }

        return 0;
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        var value = TryGet(element, propertyName);
        return value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetDouble(out var result)
            ? result
            : 0d;
    }
}

internal sealed record ParsedChampSelectMember(
    string StableKey,
    string? Puuid,
    int Team,
    int ChampionId,
    bool IsAnonymous);

internal sealed record ParsedGameStats(double GameTimeSeconds, string GameMode);

internal sealed record ParsedLiveItem(int ItemId, int Count, int ReportedPrice);

internal sealed record ParsedLivePlayer(
    string RiotId,
    int Team,
    string ChampionName,
    int ChampionId,
    int Kills,
    int Deaths,
    int Assists,
    int CreepScore,
    int Level,
    IReadOnlyList<ParsedLiveItem> Items);
