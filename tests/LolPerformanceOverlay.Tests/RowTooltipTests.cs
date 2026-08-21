using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Presentation;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class RowTooltipTests
{
    [Fact]
    public void ResolvedRankProducesExactlyThreeLines()
    {
        var lines = RowTooltip.Compose(Player(rank: new OfficialRankDisplay(
            "D4",
            string.Empty,
            IsStale: false,
            "鑽石 IV · 42 LP（單雙排）"))).Split('\n');

        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void FirstLineCarriesBothTheRiotIdAndTheChampion()
    {
        // The row cell shows one or the other depending on the display setting, so hovering is
        // how the reader sees whichever one is not on screen -- both must always be here.
        var lines = RowTooltip.Compose(Player()).Split('\n');

        Assert.Contains("Synthetic Row 01#FAKE01", lines[0]);
        Assert.Contains("Ahri", lines[0]);
    }

    [Fact]
    public void SecondLineCarriesTheLabelAndConfidence()
    {
        var lines = RowTooltip.Compose(Player()).Split('\n');

        Assert.Contains("穩定", lines[1]);
        Assert.Contains("中信心", lines[1]);
    }

    [Fact]
    public void AnonymousPlayerRevealsNoIdentityOnHoverEither()
    {
        // A tooltip is not a lesser surface than the row cell. PlayerNameDisplay refuses to
        // reveal an anonymous seat; this must refuse identically, or hover text becomes the
        // hole in the promise that the program never restores players Riot hid.
        var tooltip = RowTooltip.Compose(Player(displayName: "Synthetic Row 01#FAKE01", anonymous: true));

        Assert.DoesNotContain("Synthetic Row 01", tooltip);
        Assert.DoesNotContain("FAKE01", tooltip);
        Assert.StartsWith("Ahri", tooltip);
    }

    [Fact]
    public void BlankRiotIdFallsBackToTheChampionRatherThanLeavingADanglingSeparator()
    {
        var tooltip = RowTooltip.Compose(Player(displayName: "   "));

        Assert.StartsWith("Ahri", tooltip);
        Assert.DoesNotContain("·", tooltip.Split('\n')[0]);
    }

    [Fact]
    public void FailedLookupPutsThePlainLanguageReasonOnTheThirdLine()
    {
        var lines = RowTooltip.Compose(Player(rank: new OfficialRankDisplay(
            "—",
            "查詢額度已經用完，稍後會恢復",
            IsStale: false,
            "查詢額度已經用完，稍後會恢復"))).Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Equal("查詢額度已經用完，稍後會恢復", lines[2]);
    }

    [Fact]
    public void NoLookupResultYetLeavesTwoLinesRatherThanAnEmptyThird()
    {
        var lines = RowTooltip.Compose(Player()).Split('\n');

        Assert.Equal(2, lines.Length);
    }

    private static OverlayPlayer Player(
        string? displayName = "Synthetic Row 01#FAKE01",
        bool anonymous = false,
        OfficialRankDisplay? rank = null) =>
        new(
            "row-1",
            displayName ?? string.Empty,
            100,
            "Ahri",
            null,
            anonymous,
            55.0,
            "穩定",
            PerformanceConfidence.Medium,
            OfficialRank: rank);
}
