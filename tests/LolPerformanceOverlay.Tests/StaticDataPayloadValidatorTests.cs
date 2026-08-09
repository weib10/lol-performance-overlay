using System.Text.Json;
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
}
