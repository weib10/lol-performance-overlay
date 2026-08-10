using System.Buffers;
using System.Text;

namespace LolPerformanceOverlay.Core;

/// <summary>
/// Reads untrusted HTTP or cache content without allowing a missing Content-Length header to bypass
/// the product's memory envelope.
/// </summary>
public static class BoundedStreamReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task<string> ReadUtf8Async(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        using var buffer = await ReadBufferAsync(source, maximumBytes, cancellationToken).ConfigureAwait(false);
        return StrictUtf8.GetString(buffer.Buffer, 0, buffer.Length);
    }

    public static async Task<byte[]> ReadBytesAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        using var buffer = await ReadBufferAsync(source, maximumBytes, cancellationToken).ConfigureAwait(false);
        return buffer.Buffer.AsSpan(0, buffer.Length).ToArray();
    }

    private static async Task<RentedBuffer> ReadBufferAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maximumBytes, 16 * 1024));
        var length = 0;
        try
        {
            while (true)
            {
                if (length >= maximumBytes)
                {
                    var probe = new byte[1];
                    if (await source.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
                    {
                        throw new InvalidDataException($"Payload exceeds the {maximumBytes}-byte limit.");
                    }

                    break;
                }

                if (length == buffer.Length)
                {
                    var nextLength = (int)Math.Min(maximumBytes, (long)buffer.Length * 2);
                    var expanded = ArrayPool<byte>.Shared.Rent(nextLength);
                    buffer.AsSpan(0, length).CopyTo(expanded);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = expanded;
                }

                var read = await source.ReadAsync(
                    buffer.AsMemory(length, Math.Min(buffer.Length - length, maximumBytes - length)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            return new RentedBuffer(buffer, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private sealed class RentedBuffer(byte[] buffer, int length) : IDisposable
    {
        public byte[] Buffer { get; } = buffer;
        public int Length { get; } = length;

        public void Dispose() => ArrayPool<byte>.Shared.Return(Buffer);
    }
}
