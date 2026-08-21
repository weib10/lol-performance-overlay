namespace LolPerformanceOverlay.Core.Presentation;

/// <summary>
/// The hover text for one row of the Expanded ten-player panel, composed here in Core so it is
/// a string a test can assert rather than something a WPF event handler assembles.
/// Exactly three lines, because the panel is read mid-game and the previous six-line version
/// was more than anyone stops to read during a fight:
/// <list type="number">
/// <item>who this is -- Riot ID and champion, both, whichever one the row itself is showing</item>
/// <item>this program's own reading of the current game -- label and confidence</item>
/// <item>the official rank, with the ladder it belongs to</item>
/// </list>
/// Line 1 shows both identities on purpose: the row cell displays one or the other depending on
/// <see cref="PlayerNameDisplayMode"/>, so hovering is how you see the one you cannot.
/// </summary>
public static class RowTooltip
{
    public static string Compose(OverlayPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var lines = new List<string>(3) { IdentityLine(player), ReadingLine(player) };
        // Line 3 exists only once a lookup has produced something to say. A player with no
        // result yet gets a two-line tooltip rather than an empty third line -- there is no
        // honest text for "we have not looked yet" that is worth a row of its own.
        if (player.OfficialRank?.TooltipText is { Length: > 0 } rankLine)
        {
            lines.Add(rankLine);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// An anonymous seat never has an identity revealed here, exactly as
    /// <see cref="PlayerNameDisplay.Resolve"/> refuses to reveal one in the row cell. A tooltip
    /// is not a lesser surface -- AGENTS.md and SECURITY.md both promise the program never
    /// restores players Riot deliberately hid, and hover text would be a hole in that promise.
    /// </summary>
    private static string IdentityLine(OverlayPlayer player)
    {
        if (player.IsAnonymous || string.IsNullOrWhiteSpace(player.DisplayName))
        {
            return player.ChampionName;
        }

        return $"{player.DisplayName} · {player.ChampionName}";
    }

    private static string ReadingLine(OverlayPlayer player) => player.PerformanceLabel is null
        ? player.IsAnonymous ? "匿名" : "尚未開始"
        : $"{player.PerformanceLabel} · {ConfidenceText(player.Confidence)}";

    private static string ConfidenceText(PerformanceConfidence? confidence) => confidence switch
    {
        PerformanceConfidence.High => "高信心",
        PerformanceConfidence.Medium => "中信心",
        PerformanceConfidence.Low => "低信心",
        _ => "資料不足"
    };
}
