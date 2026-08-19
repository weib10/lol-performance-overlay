using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Presentation;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class HistoricalPanelPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComposedTextNeverMentionsRankWording()
    {
        // Every row already carries its own official rank short code and tooltip (issues
        // #7-#9); this panel would only repeat that if any of this vocabulary leaked back in.
        var profile = HistoricalTestData.Profile(HistoricalTestData.Player(1), HistoricalQueue.RankedSolo, Now);

        var display = HistoricalPanelPresenter.Describe(profile);

        var rankWords = new[] { "牌位", "Rank", "rank", "官方", "段位" };
        foreach (var word in rankWords)
        {
            Assert.DoesNotContain(word, display.MetaText);
            Assert.DoesNotContain(word, display.RecentFormText);
        }
    }

    [Fact]
    public void MetaTextCarriesSourceQueueSampleCountFetchTimeAndConfidence()
    {
        var profile = HistoricalTestData.Profile(
            HistoricalTestData.Player(1),
            HistoricalQueue.RankedSolo,
            Now,
            sampleCount: 20);

        var display = HistoricalPanelPresenter.Describe(profile);

        Assert.Contains(profile.Source.DisplayName, display.MetaText);
        Assert.Contains(profile.Queue.DisplayName, display.MetaText);
        Assert.Contains("20 場", display.MetaText);
        Assert.Contains(profile.FetchedAt.ToLocalTime().ToString("MM/dd HH:mm"), display.MetaText);
        Assert.Contains("高信心", display.MetaText);
    }

    [Fact]
    public void ProfileWithPlayStyleYieldsStyleWording()
    {
        var profile = HistoricalTestData.Profile(
            HistoricalTestData.Player(1),
            HistoricalQueue.RankedSolo,
            Now,
            includePlayStyle: true);
        Assert.NotNull(profile.PlayStyle);

        var display = HistoricalPanelPresenter.Describe(profile);

        Assert.NotEmpty(display.RecentFormText);
        Assert.Contains("激進", display.RecentFormText);
        Assert.Contains("英雄池", display.RecentFormText);
    }

    [Fact]
    public void NullPlayStyleYieldsNoStyleWordingAtAll()
    {
        // A rank-only source (one ranked-entries lookup, no match history) has nothing to
        // derive a style from -- HistoricalTestData.Profile(includePlayStyle: false) models
        // exactly that, matching what RiotHistoricalProfileTransport produces.
        var profile = HistoricalTestData.Profile(
            HistoricalTestData.Player(1),
            HistoricalQueue.RankedSolo,
            Now,
            sampleCount: 0,
            includePlayStyle: false);
        Assert.Null(profile.PlayStyle);

        var display = HistoricalPanelPresenter.Describe(profile);

        // The champions half of the line may still be there; what must not appear is any band
        // reading, because there is no match history behind one.
        foreach (var word in new[] { "激進", "生存", "團隊", "發育", "英雄池", "平衡", "偏高", "偏低" })
        {
            Assert.DoesNotContain(word, display.RecentFormText);
        }

        Assert.True(display.HasContent);
    }

    [Fact]
    public void RecentFormTextKeepsTheMostPlayedChampionsTheRowRankCannotCarry()
    {
        var profile = HistoricalTestData.Profile(
            HistoricalTestData.Player(1),
            HistoricalQueue.RankedSolo,
            Now,
            sampleCount: 20);

        var display = HistoricalPanelPresenter.Describe(profile);

        Assert.Contains("常用：", display.RecentFormText);
        Assert.Contains(profile.CommonChampions[0].ChampionName, display.RecentFormText);
    }

    [Fact]
    public void NoProfileYieldsEmptyOutputSoTheCallerCanCollapseTheBlock()
    {
        var display = HistoricalPanelPresenter.Describe(null);

        Assert.Same(HistoricalPanelDisplay.Empty, display);
        Assert.False(display.HasContent);
        Assert.Equal(string.Empty, display.MetaText);
        Assert.Equal(string.Empty, display.RecentFormText);
    }
}
