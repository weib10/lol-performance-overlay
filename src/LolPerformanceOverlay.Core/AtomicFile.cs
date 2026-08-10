using System.Text;

namespace LolPerformanceOverlay.Core;

/// <summary>
/// Writes a complete replacement beside the destination and only then swaps it into place.
/// A cancellation or process failure before the final move leaves the previous file intact.
/// </summary>
public static class AtomicFile
{
    // Windows does not guarantee that concurrent overwrite moves to the same destination can all
    // succeed. Writes are infrequent (settings and static cache files), so one process-wide gate is
    // simpler and strictly bounded compared with a per-path semaphore dictionary.
    private static readonly SemaphoreSlim ReplacementGate = new(1, 1);

    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        await ReplacementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("The destination must have a parent directory.", nameof(path));
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    contents,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                TryDeleteTemporary(temporaryPath);
            }
        }
        finally
        {
            ReplacementGate.Release();
        }
    }

    public static async Task WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await ReplacementGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("The destination must have a parent directory.", nameof(path));
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 16 * 1024,
                                 FileOptions.Asynchronous))
                {
                    await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                TryDeleteTemporary(temporaryPath);
            }
        }
        finally
        {
            ReplacementGate.Release();
        }
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stale sibling is safer than damaging the last known-good destination.
        }
    }
}
