using System.Text.Json;
using System.Buffers.Binary;
using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class StaticDataPayloadValidatorTests
{
    private const string CompletePng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII=";

    [Fact]
    public void CompleteDataObjectIsAccepted() =>
        StaticDataPayloadValidator.RequireDataObject("{\"data\":{\"Synthetic\":{}}}", "synthetic fixture");

    [Theory]
    [InlineData("")]
    [InlineData("{\"data\":")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"version\":\"synthetic\"}")]
    public void TruncatedOrWrongShapePayloadIsRejected(string json)
    {
        Assert.ThrowsAny<Exception>(() =>
            StaticDataPayloadValidator.RequireDataObject(json, "synthetic fixture"));
    }

    [Fact]
    public void CompletePngRequiresHeaderImageDataAndTerminatingChunk()
    {
        var complete = Convert.FromBase64String(CompletePng);

        Assert.True(PngPayloadValidator.IsComplete(complete));
        Assert.False(PngPayloadValidator.IsComplete(complete.AsSpan(0, 8)));
        Assert.False(PngPayloadValidator.IsComplete(complete.AsSpan(0, complete.Length - 1)));

        var corruptHeader = complete.ToArray();
        corruptHeader[16] = 0;
        corruptHeader[17] = 0;
        corruptHeader[18] = 0;
        corruptHeader[19] = 0;
        Assert.False(PngPayloadValidator.IsComplete(corruptHeader));
    }

    [Fact]
    public void ChampionPngEnvelopeRejectsOversizedDimensionsAndPayloads()
    {
        var oversizedDimensions = Convert.FromBase64String(CompletePng);
        BinaryPrimitives.WriteUInt32BigEndian(oversizedDimensions.AsSpan(16, 4), 513);
        var headerCrc = ComputeCrc32(oversizedDimensions.AsSpan(12, 17));
        BinaryPrimitives.WriteUInt32BigEndian(oversizedDimensions.AsSpan(29, 4), headerCrc);

        Assert.False(PngPayloadValidator.IsComplete(oversizedDimensions));
        Assert.False(PngPayloadValidator.IsComplete(new byte[PngPayloadValidator.MaximumEncodedBytes + 1]));
        Assert.InRange(PngPayloadValidator.MaximumEncodedBytes, 1, 4 * 1024 * 1024);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xffffffffu;
    }
}
