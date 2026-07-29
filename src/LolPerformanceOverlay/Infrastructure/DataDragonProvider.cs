using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.Infrastructure;

public sealed class DataDragonProvider : IStaticGameDataProvider, IDisposable
{
    private const string VersionsUrl = "https://ddragon.leagueoflegends.com/api/versions.json";
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, ChampionDescriptor> _championsByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChampionDescriptor> _championsById = new();
    private readonly ConcurrentDictionary<int, int> _itemGold = new();
    private string? _version;

    public DataDragonProvider()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LolPerformanceOverlay",
            "cache");
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LolPerformanceOverlay/1.0");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var cacheChampionPath = Path.Combine(_cacheDirectory, "champion.json");
        var cacheItemPath = Path.Combine(_cacheDirectory, "item.json");
        var cacheVersionPath = Path.Combine(_cacheDirectory, "version.txt");

        string? championJson = null;
        string? itemJson = null;
        if (File.Exists(cacheChampionPath) && File.Exists(cacheItemPath))
        {
            championJson = await File.ReadAllTextAsync(cacheChampionPath, cancellationToken);
            itemJson = await File.ReadAllTextAsync(cacheItemPath, cancellationToken);
            _version = File.Exists(cacheVersionPath)
                ? (await File.ReadAllTextAsync(cacheVersionPath, cancellationToken)).Trim()
                : null;
            ParseChampions(championJson);
            ParseItems(itemJson);

            if (File.GetLastWriteTimeUtc(cacheChampionPath) >= DateTime.UtcNow.AddHours(-24))
            {
                return;
            }
        }

        try
        {
            var versionsJson = await _httpClient.GetStringAsync(VersionsUrl, cancellationToken);
            _version = JsonSerializer.Deserialize<string[]>(versionsJson)?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(_version))
            {
                throw new InvalidDataException("Data Dragon did not return a patch version.");
            }

            championJson = await _httpClient.GetStringAsync(
                $"https://ddragon.leagueoflegends.com/cdn/{_version}/data/zh_TW/champion.json",
                cancellationToken);
            itemJson = await _httpClient.GetStringAsync(
                $"https://ddragon.leagueoflegends.com/cdn/{_version}/data/zh_TW/item.json",
                cancellationToken);

            await File.WriteAllTextAsync(cacheChampionPath, championJson, cancellationToken);
            await File.WriteAllTextAsync(cacheItemPath, itemJson, cancellationToken);
            await File.WriteAllTextAsync(cacheVersionPath, _version, cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // Already loaded cached data above when available.
        }

        if (!string.IsNullOrWhiteSpace(championJson))
        {
            ParseChampions(championJson);
        }

        if (!string.IsNullOrWhiteSpace(itemJson))
        {
            ParseItems(itemJson);
        }
    }

    public ChampionDescriptor ResolveChampion(string championName, int championId = 0)
    {
        if (championId > 0 && _championsById.TryGetValue(championId, out var byId))
        {
            return byId;
        }

        var normalized = NormalizeChampionKey(championName);
        if (_championsByName.TryGetValue(normalized, out var byName))
        {
            return byName;
        }

        return new ChampionDescriptor(
            championId,
            string.IsNullOrWhiteSpace(normalized) ? "Unknown" : normalized,
            string.IsNullOrWhiteSpace(championName) ? "未知英雄" : championName,
            [ChampionArchetype.Fighter]);
    }

    public int GetItemGoldValue(int itemId) => _itemGold.GetValueOrDefault(itemId);

    public async Task<string?> EnsureChampionIconAsync(
        ChampionDescriptor champion,
        CancellationToken cancellationToken)
    {
        if (champion.Key == "Unknown")
        {
            return null;
        }

        var filePath = Path.Combine(_cacheDirectory, "icons", $"{champion.Key}.png");
        if (File.Exists(filePath))
        {
            return filePath;
        }

        if (string.IsNullOrWhiteSpace(_version))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var bytes = await _httpClient.GetByteArrayAsync(
                $"https://ddragon.leagueoflegends.com/cdn/{_version}/img/champion/{champion.Key}.png",
                cancellationToken);
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
            return filePath;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private void ParseChampions(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return;
        }

        foreach (var property in data.EnumerateObject())
        {
            var element = property.Value;
            var id = int.TryParse(GetString(element, "key"), out var parsedId) ? parsedId : 0;
            var key = GetString(element, "id") ?? property.Name;
            var name = GetString(element, "name") ?? key;
            var tags = element.TryGetProperty("tags", out var tagElement)
                ? tagElement.EnumerateArray()
                    .Select(tag => ParseArchetype(tag.GetString()))
                    .Where(tag => tag.HasValue)
                    .Select(tag => tag!.Value)
                    .ToArray()
                : Array.Empty<ChampionArchetype>();
            var descriptor = new ChampionDescriptor(
                id,
                key,
                name,
                tags.Length == 0 ? [ChampionArchetype.Fighter] : tags);

            _championsById[id] = descriptor;
            _championsByName[NormalizeChampionKey(key)] = descriptor;
            _championsByName[NormalizeChampionKey(name)] = descriptor;
            _championsByName[NormalizeChampionKey(property.Name)] = descriptor;
        }
    }

    private void ParseItems(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return;
        }

        foreach (var property in data.EnumerateObject())
        {
            if (!int.TryParse(property.Name, out var itemId))
            {
                continue;
            }

            var element = property.Value;
            var excluded = false;
            if (element.TryGetProperty("tags", out var tags))
            {
                excluded = tags.EnumerateArray().Any(tag =>
                    tag.GetString() is "Consumable" or "Trinket");
            }

            var total = element.TryGetProperty("gold", out var gold) &&
                        gold.TryGetProperty("total", out var totalElement) &&
                        totalElement.TryGetInt32(out var parsedTotal)
                ? parsedTotal
                : 0;
            _itemGold[itemId] = excluded ? 0 : Math.Max(total, 0);
        }
    }

    private static ChampionArchetype? ParseArchetype(string? value) => value switch
    {
        "Marksman" => ChampionArchetype.Marksman,
        "Assassin" => ChampionArchetype.Assassin,
        "Mage" => ChampionArchetype.Mage,
        "Fighter" => ChampionArchetype.Fighter,
        "Tank" => ChampionArchetype.Tank,
        "Support" => ChampionArchetype.Support,
        _ => null
    };

    private static string NormalizeChampionKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Equals("Fiddlesticks", StringComparison.OrdinalIgnoreCase)
            ? "FiddleSticks"
            : normalized;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;
}
