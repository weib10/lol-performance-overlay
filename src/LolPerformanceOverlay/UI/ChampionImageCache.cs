using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.UI;

/// <summary>
/// Owns decoded WPF champion images for the lifetime of the process. The session source
/// prepares files off the UI thread; this adapter performs at most one WPF decode per path.
/// </summary>
internal sealed class ChampionImageCache
{
    private const int DecodePixelWidth = 64;
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> _images =
        new(StringComparer.OrdinalIgnoreCase);
    private long _cacheHits;
    private long _decodeCount;

    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long DecodeCount => Interlocked.Read(ref _decodeCount);

    public async Task<ImageSource?> GetAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidate = new Lazy<Task<ImageSource?>>(
            () => Task.Run(() => Decode(path)),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var cached = _images.GetOrAdd(path, candidate);
        if (!ReferenceEquals(candidate, cached))
        {
            Interlocked.Increment(ref _cacheHits);
        }
        ImageSource? image;
        try
        {
            image = await cached.Value.ConfigureAwait(false);
        }
        catch
        {
            image = null;
        }
        if (image is null)
        {
            ((ICollection<KeyValuePair<string, Lazy<Task<ImageSource?>>>>)_images)
                .Remove(new KeyValuePair<string, Lazy<Task<ImageSource?>>>(path, cached));
        }

        return image;
    }

    private ImageSource? Decode(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (new FileInfo(path).Length > PngPayloadValidator.MaximumEncodedBytes)
            {
                TryDeleteInvalidCacheFile(path);
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.DecodePixelWidth = DecodePixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            Interlocked.Increment(ref _decodeCount);
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or
                                          NotSupportedException or
                                          ArgumentException or
                                          InvalidOperationException)
        {
            TryDeleteInvalidCacheFile(path);
            return null;
        }
    }

    private static void TryDeleteInvalidCacheFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A locked cache file may be retried after a visual generation rebuild; failure stays isolated.
        }
    }
}
