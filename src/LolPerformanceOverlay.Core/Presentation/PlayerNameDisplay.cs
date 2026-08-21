namespace LolPerformanceOverlay.Core.Presentation;

/// <summary>
/// What a player row shows beside the avatar in the Expanded panel. <see cref="ChampionName"/>
/// is today's behaviour and stays the default; <see cref="RiotId"/> is the opt-in alternative
/// requested in "英雄頭像旁邊現在顯示的是英雄，但我需要設定中可以調顯示 ID 還是英雄名稱".
/// </summary>
public enum PlayerNameDisplayMode
{
    ChampionName = 0,
    RiotId = 1
}

/// <summary>
/// Decides the text a player row shows beside the avatar, and the label for the column header
/// above it. Kept here rather than inline in the WPF adapter so the anonymity guarantee below
/// is one small, unit-tested function instead of a branch buried in a UI event handler --
/// the same seam convention as <see cref="OfficialRankAttachment"/>.
///
/// AGENTS.md and SECURITY.md both promise this program never restores an identity Riot itself
/// chose to hide. <see cref="OverlayPlayer.IsAnonymous"/> seats therefore always resolve to the
/// champion name here regardless of the requested mode: <see cref="PlayerNameDisplayMode.RiotId"/>
/// is a display preference for seats Riot has already revealed, never a way to see through a
/// hidden one. A missing or blank <see cref="OverlayPlayer.DisplayName"/> -- which should not
/// happen for a revealed player, but nothing upstream guarantees it -- falls back to the champion
/// name too, so the cell is never left blank.
/// </summary>
public static class PlayerNameDisplay
{
    public static string Resolve(OverlayPlayer player, PlayerNameDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (mode == PlayerNameDisplayMode.RiotId &&
            !player.IsAnonymous &&
            !string.IsNullOrWhiteSpace(player.DisplayName))
        {
            return player.DisplayName;
        }

        return player.ChampionName;
    }

    /// <summary>
    /// Names what the column actually contains under the given mode, so the header always
    /// matches the cells beneath it instead of drifting out of sync with a separate literal.
    /// </summary>
    public static string ColumnHeader(PlayerNameDisplayMode mode) =>
        mode == PlayerNameDisplayMode.RiotId ? "Riot ID" : "英雄";
}
