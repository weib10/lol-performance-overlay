using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.Infrastructure;

public sealed class DataDragonProvider : IStaticGameDataProvider, IDisposable
{
    private const int MaximumVersionsBytes = 256 * 1024;
    private const int MaximumStaticJsonBytes = 8 * 1024 * 1024;
    private const int MaximumChampionCount = 512;
    private const int MaximumItemCount = 4_096;
    private static readonly TimeSpan IconFailureBackoff = TimeSpan.FromSeconds(30);
    private static readonly Uri VersionsUrl = DataDragonUri("api/versions.json");
    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, ChampionDescriptor> _championsByName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ChampionDescriptor> _championsById = new();
    private readonly ConcurrentDictionary<int, int> _itemGold = new();
    private readonly ConcurrentDictionary<string, byte> _validatedIconPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _iconRetryAfter =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _version;

    public DataDragonProvider()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LolPerformanceOverlay",
            "cache");
        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
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
                championJson = await ReadBoundedFileAsync(
                    cacheChampionPath,
                    MaximumStaticJsonBytes,
                    cancellationToken);
                itemJson = await ReadBoundedFileAsync(
                    cacheItemPath,
                    MaximumStaticJsonBytes,
                    cancellationToken);
                _version = File.Exists(cacheVersionPath)
                    ? (await ReadBoundedFileAsync(cacheVersionPath, 256, cancellationToken)).Trim()
                    : null;
                if (!StaticAssetPolicy.IsVersion(_version))
                {
                    throw new InvalidDataException("Cached Data Dragon version is invalid.");
                }

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
            var versionsJson = await GetBoundedStringAsync(
                VersionsUrl,
                MaximumVersionsBytes,
                cancellationToken);
            var downloadedVersion = ParseLatestVersion(versionsJson);
            if (string.IsNullOrWhiteSpace(downloadedVersion) || !StaticAssetPolicy.IsVersion(downloadedVersion))
            {
                throw new InvalidDataException("Data Dragon did not return a patch version.");
            }

            _version = downloadedVersion;

            var downloadedChampionJson = await GetBoundedStringAsync(
                DataDragonUri($"cdn/{_version}/data/zh_TW/champion.json"),
                MaximumStaticJsonBytes,
                cancellationToken);
            var downloadedItemJson = await GetBoundedStringAsync(
                DataDragonUri($"cdn/{_version}/data/zh_TW/item.json"),
                MaximumStaticJsonBytes,
                cancellationToken);
            StaticDataPayloadValidator.RequireDataObject(downloadedChampionJson, "champion response");
            StaticDataPayloadValidator.RequireDataObject(downloadedItemJson, "item response");
            ReplaceParsedData(downloadedChampionJson, downloadedItemJson);
            championJson = downloadedChampionJson;
            itemJson = downloadedItemJson;

            await AtomicFile.WriteAllTextAsync(cacheChampionPath, downloadedChampionJson, cancellationToken);
            await AtomicFile.WriteAllTextAsync(cacheItemPath, downloadedItemJson, cancellationToken);
            await AtomicFile.WriteAllTextAsync(cacheVersionPath, downloadedVersion, cancellationToken);
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

        if (_championsByName.TryGetValue(championName, out var byExactName))
        {
            return byExactName;
        }

        var normalized = NormalizeChampionKey(championName);
        if (_championsByName.TryGetValue(normalized, out var byName))
        {
            return byName;
        }

        return new ChampionDescriptor(
            championId,
            "Unknown",
            string.IsNullOrWhiteSpace(championName) ? "未知英雄" : championName,
            [ChampionArchetype.Fighter]);
    }

    public int GetItemGoldValue(int itemId) => _itemGold.GetValueOrDefault(itemId);

    public ValueTask<string?> EnsureChampionIconAsync(
        ChampionDescriptor champion,
        CancellationToken cancellationToken)
    {
        if (champion.Key == "Unknown")
        {
            return ValueTask.FromResult<string?>(null);
        }

        if (!StaticAssetPolicy.IsChampionKey(champion.Key))
        {
            return ValueTask.FromResult<string?>(null);
        }

        var iconsDirectory = Path.Combine(_cacheDirectory, "icons");
        if (!StaticAssetPolicy.TryResolveChildPath(
                iconsDirectory,
                $"{champion.Key}.png",
                out var filePath))
        {
            return ValueTask.FromResult<string?>(null);
        }

        // Once this process has validated or atomically written the asset, trust the process-lifetime
        // cache instead of issuing ten File.Exists calls for every live frame.
        if (_validatedIconPaths.ContainsKey(filePath))
        {
            return ValueTask.FromResult<string?>(filePath);
        }

        return new ValueTask<string?>(EnsureChampionIconCoreAsync(champion, filePath, cancellationToken));
    }

    private async Task<string?> EnsureChampionIconCoreAsync(
        ChampionDescriptor champion,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (_iconRetryAfter.TryGetValue(filePath, out var retryAfter) && retryAfter > DateTimeOffset.UtcNow)
        {
            return null;
        }

        if (File.Exists(filePath))
        {
            if (await IsCompletePngAsync(filePath, cancellationToken))
            {
                _validatedIconPaths[filePath] = 0;
                return filePath;
            }

            if (!TryDeleteInvalidIcon(filePath))
            {
                _iconRetryAfter[filePath] = DateTimeOffset.UtcNow + IconFailureBackoff;
                return null;
            }
        }

        _validatedIconPaths.TryRemove(filePath, out _);

        if (!StaticAssetPolicy.IsVersion(_version))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var bytes = await GetBoundedBytesAsync(
                DataDragonUri($"cdn/{_version}/img/champion/{champion.Key}.png"),
                PngPayloadValidator.MaximumEncodedBytes,
                cancellationToken);
            if (!PngPayloadValidator.IsComplete(bytes))
            {
                _iconRetryAfter[filePath] = DateTimeOffset.UtcNow + IconFailureBackoff;
                return null;
            }

            await AtomicFile.WriteAllBytesAsync(filePath, bytes, cancellationToken);
            _validatedIconPaths[filePath] = 0;
            _iconRetryAfter.TryRemove(filePath, out _);
            return filePath;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            _iconRetryAfter[filePath] = DateTimeOffset.UtcNow + IconFailureBackoff;
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
            if (new FileInfo(path).Length > PngPayloadValidator.MaximumEncodedBytes)
            {
                return false;
            }

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

    private static bool TryDeleteInvalidIcon(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            // The caller enters a bounded retry backoff; a locked corrupt file must not turn into
            // repeated full-file validation on every live frame.
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

        var inspectedCount = 0;
        foreach (var property in data.EnumerateObject())
        {
            if (inspectedCount++ >= MaximumChampionCount)
            {
                break;
            }

            var element = property.Value;
            var id = int.TryParse(GetString(element, "key"), out var parsedId) ? parsedId : 0;
            var key = GetString(element, "id") ?? property.Name;
            if (!StaticAssetPolicy.IsChampionKey(key))
            {
                continue;
            }
            var name = GetString(element, "name");
            name = string.IsNullOrWhiteSpace(name) || name.Length > 128 ? key : name;
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

        var inspectedCount = 0;
        foreach (var property in data.EnumerateObject())
        {
            if (inspectedCount++ >= MaximumItemCount)
            {
                break;
            }

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

    private static string? ParseLatestVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var value in document.RootElement.EnumerateArray())
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        return null;
    }

    private static async Task<string> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        RequireFileWithinLimit(path, maximumBytes);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await BoundedStreamReader.ReadUtf8Async(stream, maximumBytes, cancellationToken);
    }

    private async Task<string> GetBoundedStringAsync(
        Uri destination,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_httpClient.Timeout);
        var requestToken = deadline.Token;
        NetworkDestinationPolicy.RequireAllowed(destination, NetworkDestinationPurpose.RuntimeData);
        using var response = await _httpClient.GetAsync(
            destination,
            HttpCompletionOption.ResponseHeadersRead,
            requestToken);
        response.EnsureSuccessStatusCode();
        RequireContentLengthWithinLimit(response.Content.Headers.ContentLength, maximumBytes);
        await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
        return await BoundedStreamReader.ReadUtf8Async(stream, maximumBytes, requestToken);
    }

    private async Task<byte[]> GetBoundedBytesAsync(
        Uri destination,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_httpClient.Timeout);
        var requestToken = deadline.Token;
        NetworkDestinationPolicy.RequireAllowed(destination, NetworkDestinationPurpose.RuntimeData);
        using var response = await _httpClient.GetAsync(
            destination,
            HttpCompletionOption.ResponseHeadersRead,
            requestToken);
        response.EnsureSuccessStatusCode();
        RequireContentLengthWithinLimit(response.Content.Headers.ContentLength, maximumBytes);
        await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
        return await BoundedStreamReader.ReadBytesAsync(stream, maximumBytes, requestToken);
    }

    private static void RequireContentLengthWithinLimit(long? contentLength, int maximumBytes)
    {
        if (contentLength > maximumBytes)
        {
            throw new InvalidDataException($"Static-data response exceeds the {maximumBytes}-byte limit.");
        }
    }

    private static void RequireFileWithinLimit(string path, int maximumBytes)
    {
        if (new FileInfo(path).Length > maximumBytes)
        {
            throw new InvalidDataException($"Static-data cache exceeds the {maximumBytes}-byte limit.");
        }
    }
}
