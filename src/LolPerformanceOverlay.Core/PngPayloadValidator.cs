using System.Buffers.Binary;
using System.IO.Compression;

namespace LolPerformanceOverlay.Core;

/// <summary>
/// Rejects truncated, corrupt, malformed, and decompression-bomb PNG cache entries before the
/// Windows decoder sees them. The supported envelope includes every non-interlaced PNG color type
/// and bit depth permitted by the specification, which covers Data Dragon champion icons.
/// </summary>
public static class PngPayloadValidator
{
    public const int MaximumEncodedBytes = 2 * 1024 * 1024;
    private const int MaximumDimension = 512;
    private const long MaximumDecodedBytes = 4L * 1024 * 1024;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsComplete(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Signature.Length || payload.Length > MaximumEncodedBytes ||
            !payload[..Signature.Length].SequenceEqual(Signature))
        {
            return false;
        }

        try
        {
            return ValidateChunksAndPixels(payload);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool ValidateChunksAndPixels(ReadOnlySpan<byte> payload)
    {
        var offset = Signature.Length;
        var sawHeader = false;
        var sawPalette = false;
        var sawImageData = false;
        var imageDataEnded = false;
        var width = 0;
        var height = 0;
        var bitsPerPixel = 0;
        using var compressedPixels = new MemoryStream(Math.Min(payload.Length, MaximumEncodedBytes));

        while (offset <= payload.Length - 12)
        {
            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
            if (lengthValue > int.MaxValue)
            {
                return false;
            }

            var chunkLength = (int)lengthValue;
            var chunkEnd = (long)offset + 12L + chunkLength;
            if (chunkEnd > payload.Length)
            {
                return false;
            }

            var type = payload.Slice(offset + 4, 4);
            var data = payload.Slice(offset + 8, chunkLength);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset + 8 + chunkLength, 4));
            if (ComputeCrc32(type, data) != expectedCrc)
            {
                return false;
            }

            if (!sawHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || chunkLength != 13 ||
                    !TryReadHeader(data, out width, out height, out bitsPerPixel))
                {
                    return false;
                }

                sawHeader = true;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return false;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (sawImageData || chunkLength == 0 || chunkLength % 3 != 0 || chunkLength > 768)
                {
                    return false;
                }

                sawPalette = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (imageDataEnded || chunkLength == 0)
                {
                    return false;
                }

                sawImageData = true;
                if (compressedPixels.Length + chunkLength > MaximumEncodedBytes)
                {
                    return false;
                }

                compressedPixels.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (!sawImageData || chunkLength != 0 || chunkEnd != payload.Length ||
                    !PaletteRequirementSatisfied(payload, sawPalette))
                {
                    return false;
                }

                compressedPixels.Position = 0;
                return ValidateDecompressedPixels(
                    compressedPixels,
                    width,
                    height,
                    bitsPerPixel);
            }
            else
            {
                imageDataEnded |= sawImageData;
                if (IsCritical(type))
                {
                    return false;
                }
            }

            offset = (int)chunkEnd;
        }

        return false;
    }

    private static bool TryReadHeader(
        ReadOnlySpan<byte> data,
        out int width,
        out int height,
        out int bitsPerPixel)
    {
        width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        var bitDepth = data[8];
        var colorType = data[9];
        bitsPerPixel = BitsPerPixel(bitDepth, colorType);
        return width is > 0 and <= MaximumDimension &&
               height is > 0 and <= MaximumDimension &&
               bitsPerPixel > 0 &&
               data[10] == 0 && data[11] == 0 && data[12] == 0;
    }

    private static int BitsPerPixel(byte bitDepth, byte colorType)
    {
        var channels = colorType switch
        {
            0 when bitDepth is 1 or 2 or 4 or 8 or 16 => 1,
            2 when bitDepth is 8 or 16 => 3,
            3 when bitDepth is 1 or 2 or 4 or 8 => 1,
            4 when bitDepth is 8 or 16 => 2,
            6 when bitDepth is 8 or 16 => 4,
            _ => 0
        };
        return channels * bitDepth;
    }

    private static bool ValidateDecompressedPixels(
        Stream compressed,
        int width,
        int height,
        int bitsPerPixel)
    {
        var bytesPerRow = checked(((long)width * bitsPerPixel + 7) / 8);
        var expectedLength = checked((bytesPerRow + 1) * height);
        if (expectedLength <= 0 || expectedLength > MaximumDecodedBytes)
        {
            return false;
        }

        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        var buffer = new byte[8192];
        var decodedLength = 0L;
        var positionInRow = 0L;
        while (true)
        {
            var read = zlib.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                if (positionInRow == 0 && buffer[index] > 4)
                {
                    return false;
                }

                positionInRow = positionInRow == bytesPerRow ? 0 : positionInRow + 1;
            }

            decodedLength += read;
            if (decodedLength > expectedLength)
            {
                return false;
            }
        }

        return decodedLength == expectedLength && positionInRow == 0;
    }

    private static bool PaletteRequirementSatisfied(ReadOnlySpan<byte> payload, bool sawPalette) =>
        payload[25] != 3 || sawPalette;

    private static bool IsCritical(ReadOnlySpan<byte> type) => (type[0] & 0x20) == 0;

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xffffffffu;
        foreach (var value in type)
        {
            crc = UpdateCrc32(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdateCrc32(crc, value);
        }

        return crc ^ 0xffffffffu;
    }

    private static uint UpdateCrc32(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }
}
