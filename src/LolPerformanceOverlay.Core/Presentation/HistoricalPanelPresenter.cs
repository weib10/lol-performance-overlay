namespace LolPerformanceOverlay.Core.Presentation;

/// <summary>
/// Already-formatted text for the Expanded panel's bottom single-player block (issue #10).
/// Every row now carries its own official rank short code and tooltip (issues #7-#9), so this
/// block would only repeat the local player's rank if it kept saying it; the rank wording is
/// gone entirely and this describes only the local player's recent-form context instead.
/// <see cref="MetaText"/> is source, queue, sample count, fetch time and confidence, composed
/// together because they answer one question -- "what is this reading based on" -- and are
/// never meaningful apart. It is empty exactly when there was no profile to describe.
/// <see cref="RecentFormText"/> is the most-played champions and the play-style bands on one
/// line. The style half stays out whenever <see cref="HistoricalProfile.PlayStyle"/> is null: a
/// rank-only source has no match history to read a style from, and AGENTS.md's historical-data
/// rule forbids inventing one to fill the gap. A player with only a rank reading gets a meta
/// line and no form line at all, not a sentence explaining the absence -- the per-row tooltip
/// already carries that nuance for #9.
/// <see cref="HasContent"/> is false only for <see cref="Empty"/>, which is what a caller gets
/// back when there is nothing left worth saying (no profile, or a failed lookup). The caller
/// (OverlayWindow.UpdateHistoryControls) collapses the whole panel in that case rather than
/// spending a permanent "unavailable" band over a live game.
/// </summary>
public sealed record HistoricalPanelDisplay(string MetaText, string RecentFormText)
{
    public static readonly HistoricalPanelDisplay Empty = new(string.Empty, string.Empty);

    public bool HasContent => MetaText.Length > 0;
}

/// <summary>
/// Composes <see cref="HistoricalPanelDisplay"/> from an already-fetched
/// <see cref="HistoricalProfile"/>. Pure text formatting, no IO, following the same convention
/// as <see cref="OfficialRankAttachment"/>: the wording lives here in Core, once, as an
/// observable string tests can assert directly -- OverlayWindow only decides visibility from
/// the result, it never composes a sentence of its own.
/// </summary>
public static class HistoricalPanelPresenter
{
    /// <summary>
    /// Describes <paramref name="profile"/> for the bottom panel. Returns
    /// <see cref="HistoricalPanelDisplay.Empty"/> when <paramref name="profile"/> is null --
    /// unresolved, unavailable, or any lookup failure all collapse to "nothing to say" here,
    /// because the per-row rank cell (see OfficialRankAttachment) already carries a
    /// friend-facing reason for those states and this block would only repeat it.
    /// </summary>
    public static HistoricalPanelDisplay Describe(HistoricalProfile? profile)
    {
        if (profile is null)
        {
            return HistoricalPanelDisplay.Empty;
        }

        var meta =
            $"來源：{profile.Source.DisplayName} · {profile.Queue.DisplayName} · {profile.SampleCount} 場 · " +
            $"{profile.FetchedAt.ToLocalTime():MM/dd HH:mm} · {ConfidenceText(profile.Confidence)}";
        // Most-played champions and style share one line. Both are recent-form context that no
        // per-row rank code can carry, so removing them along with the duplicated rank wording
        // would have cost information the panel was the only place to see.
        var parts = new List<string>(2);
        if (FormatChampions(profile.CommonChampions) is { Length: > 0 } champions)
        {
            parts.Add(champions);
        }

        if (profile.PlayStyle is { } playStyle)
        {
            parts.Add(FormatStyle(playStyle));
        }

        return new HistoricalPanelDisplay(meta, string.Join(" · ", parts));
    }

    private static string FormatChampions(IReadOnlyList<HistoricalChampionUsage> champions)
    {
        if (champions.Count == 0)
        {
            return string.Empty;
        }

        return $"常用：{string.Join("、", champions.Take(2).Select(champion => champion.ChampionName))}";
    }

    private static string FormatStyle(HistoricalPlayStyle style) =>
        $"激進 {BandText(style.Aggression.Band)}／生存 {BandText(style.Survival.Band)}／" +
        $"團隊 {BandText(style.TeamParticipation.Band)}／發育 {BandText(style.Farming.Band)}／" +
        $"英雄池 {BandText(style.ChampionPool.Band)}";

    private static string ConfidenceText(HistoricalConfidence confidence) => confidence switch
    {
        HistoricalConfidence.High => "高信心",
        HistoricalConfidence.Medium => "中信心",
        HistoricalConfidence.Low => "低信心",
        _ => "資料不足"
    };

    private static string BandText(HistoricalStyleBand band) => band switch
    {
        HistoricalStyleBand.VeryLow => "很低",
        HistoricalStyleBand.Low => "偏低",
        HistoricalStyleBand.High => "偏高",
        HistoricalStyleBand.VeryHigh => "很高",
        _ => "平衡"
    };
}
