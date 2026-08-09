using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.Infrastructure;

public sealed class DataDragonProvider : IStaticGameDataProvider, IDisposable
{
    private static readonly Uri VersionsUrl = DataDragonUri("api/versions.json");
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, ChampionDescriptor> _championsByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChampionDescriptor> _championsById = new();
    private readonly ConcurrentDictionary<int, int> _itemGold = new();
    private readonly ConcurrentDictionary<string, byte> _validatedIconPaths =
        new(StringComparer.OrdinalIgnoreCase);
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
        var productVersion = typeof(DataDragonProvider).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0] ?? "0.0.0";
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"LolPerformanceOverlay/{productVersion}");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var cacheChampionPath = Path.Combine(_cacheDirectory, "champion.json");
        var cacheItemPath = Path.Combine(_cacheDirectory, "item.json");
        var cacheVersionPath = Path.Combine(_cacheDirectory, "version.txt");

        string? championJson = null;
        string? itemJson = null;
        string? cachedVersion = null;
        var cacheLoaded = false;
        if (File.Exists(cacheChampionPath) && File.Exists(cacheItemPath))
        {
            try
            {
                championJson = await File.ReadAllTextAsync(cacheChampionPath, cancellationToken);
                itemJson = await File.ReadAllTextAsync(cacheItemPath, cancellationToken);
                _version = File.Exists(cacheVersionPath)
                    ? (await File.ReadAllTextAsync(cacheVersionPath, cancellationToken)).Trim()
                    : null;
                cachedVersion = _version;
                StaticDataPayloadValidator.RequireDataObject(championJson, "champion cache");
                StaticDataPayloadValidator.RequireDataObject(itemJson, "item cache");
                ReplaceParsedData(championJson, itemJson);
                cacheLoaded = true;

                if (File.GetLastWriteTimeUtc(cacheChampionPath) >= DateTime.UtcNow.AddHours(-24))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                championJson = null;
                itemJson = null;
                _version = null;
                ClearParsedData();
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

            var downloadedChampionJson = await _httpClient.GetStringAsync(
                DataDragonUri($"cdn/{_version}/data/zh_TW/champion.json"), cancellationToken);
            var downloadedItemJson = await _httpClient.GetStringAsync(
                DataDragonUri($"cdn/{_version}/data/zh_TW/item.json"), cancellationToken);
            StaticDataPayloadValidator.RequireDataObject(downloadedChampionJson, "champion response");
            StaticDataPayloadValidator.RequireDataObject(downloadedItemJson, "item response");
            ReplaceParsedData(downloadedChampionJson, downloadedItemJson);
            championJson = downloadedChampionJson;
            itemJson = downloadedItemJson;

            await AtomicFile.WriteAllTextAsync(cacheChampionPath, championJson, cancellationToken);
            await AtomicFile.WriteAllTextAsync(cacheItemPath, itemJson, cancellationToken);
            await AtomicFile.WriteAllTextAsync(cacheVersionPath, _version, cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            // A valid cache remains usable; malformed or unavailable network data never replaces it.
            if (cacheLoaded)
            {
                _version = cachedVersion;
            }
        }

        if (!cacheLoaded && (string.IsNullOrWhiteSpace(championJson) || string.IsNullOrWhiteSpace(itemJson)))
        {
            ClearParsedData();
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
        if (_validatedIconPaths.ContainsKey(filePath) && File.Exists(filePath))
        {
            return filePath;
        }

        if (File.Exists(filePath) && await IsCompletePngAsync(filePath, cancellationToken))
        {
            _validatedIconPaths[filePath] = 0;
            return filePath;
        }

        _validatedIconPaths.TryRemove(filePath, out _);

        if (string.IsNullOrWhiteSpace(_version))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var bytes = await _httpClient.GetByteArrayAsync(
                DataDragonUri($"cdn/{_version}/img/champion/{champion.Key}.png"), cancellationToken);
            if (!PngPayloadValidator.IsComplete(bytes))
            {
                return null;
            }

            await AtomicFile.WriteAllBytesAsync(filePath, bytes, cancellationToken);
            _validatedIconPaths[filePath] = 0;
            return filePath;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private void ReplaceParsedData(string championJson, string itemJson)
    {
        var championsByName = new Dictionary<string, ChampionDescriptor>(StringComparer.OrdinalIgnoreCase);
        var championsById = new Dictionary<int, ChampionDescriptor>();
        var itemGold = new Dictionary<int, int>();
        ParseChampions(championJson, championsByName, championsById);
        ParseItems(itemJson, itemGold);

        ClearParsedData();
        foreach (var entry in championsByName)
        {
            _championsByName[entry.Key] = entry.Value;
        }

        foreach (var entry in championsById)
        {
            _championsById[entry.Key] = entry.Value;
        }

        foreach (var entry in itemGold)
        {
            _itemGold[entry.Key] = entry.Value;
        }
    }

    private void ClearParsedData()
    {
        _championsByName.Clear();
        _championsById.Clear();
        _itemGold.Clear();
    }

    private static Uri DataDragonUri(string relativePath) =>
        NetworkDestinationPolicy.RequireAllowed(
            new Uri(new Uri("https://ddragon.leagueoflegends.com/"), relativePath),
            NetworkDestinationPurpose.RuntimeData);

    private static async Task<bool> IsCompletePngAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return PngPayloadValidator.IsComplete(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void ParseChampions(
        string json,
        IDictionary<string, ChampionDescriptor> championsByName,
        IDictionary<int, ChampionDescriptor> championsById)
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

            championsById[id] = descriptor;
            championsByName[NormalizeChampionKey(key)] = descriptor;
            championsByName[NormalizeChampionKey(name)] = descriptor;
            championsByName[NormalizeChampionKey(property.Name)] = descriptor;
        }
    }

    private static void ParseItems(string json, IDictionary<int, int> itemGold)
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
            itemGold[itemId] = excluded ? 0 : Math.Max(total, 0);
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
