namespace LolPerformanceOverlay.Core.Presentation;

[Flags]
public enum OverlaySnapshotFields
{
    None = 0,
    Phase = 1 << 0,
    Header = 1 << 1,
    Summary = 1 << 2,
    ActiveRiotId = 1 << 3,
    ActiveTeam = 1 << 4,
    LeadingTeam = 1 << 5,
    TeamGap = 1 << 6,
    Confidence = 1 << 7,
    Teams = 1 << 8,
    StatusMessage = 1 << 9,
    TeamOrder = 1 << 10,
    All = Phase | Header | Summary | ActiveRiotId | ActiveTeam | LeadingTeam |
          TeamGap | Confidence | Teams | StatusMessage | TeamOrder
}

[Flags]
public enum OverlayTeamFields
{
    None = 0,
    DisplayName = 1 << 0,
    PerformanceScore = 1 << 1,
    Players = 1 << 2,
    PlayerOrder = 1 << 3,
    All = DisplayName | PerformanceScore | Players | PlayerOrder
}

[Flags]
public enum OverlayPlayerFields
{
    None = 0,
    DisplayName = 1 << 0,
    Team = 1 << 1,
    ChampionName = 1 << 2,
    ChampionIconPath = 1 << 3,
    IsAnonymous = 1 << 4,
    PerformanceScore = 1 << 5,
    PerformanceLabel = 1 << 6,
    Confidence = 1 << 7,
    All = DisplayName | Team | ChampionName | ChampionIconPath | IsAnonymous |
          PerformanceScore | PerformanceLabel | Confidence
}

public enum SnapshotItemChange
{
    Added,
    Updated,
    Removed
}

public sealed record OverlayPlayerDiff(
    string StableKey,
    SnapshotItemChange Change,
    OverlayPlayerFields Fields);

public sealed record OverlayTeamDiff(
    int Team,
    SnapshotItemChange Change,
    OverlayTeamFields Fields,
    IReadOnlyList<OverlayPlayerDiff> Players);

public sealed record OverlaySnapshotDiff(
    OverlaySnapshotFields Fields,
    IReadOnlyList<OverlayTeamDiff> Teams)
{
    public bool HasChanges => Fields != OverlaySnapshotFields.None || Teams.Count > 0;
}

/// <summary>
/// Diffs and canonicalizes only fields observable by the overlay. Capture timestamps and collection
/// object identities are deliberately excluded, while team and player collections are matched by key.
/// </summary>
public static class VisibleSnapshot
{
    internal static bool VisibleEquals(OverlaySnapshot left, OverlaySnapshot right) =>
        DiffSnapshotFields(left, right) == OverlaySnapshotFields.None &&
        TeamsVisibleEqual(left.Teams, right.Teams);

    public static OverlaySnapshotDiff Diff(OverlaySnapshot? previous, OverlaySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            return new OverlaySnapshotDiff(
                OverlaySnapshotFields.All,
                current.Teams.Select(AddedTeam).ToArray());
        }

        var fields = DiffSnapshotFields(previous, current);
        if (TeamsVisibleEqual(previous.Teams, current.Teams))
        {
            return new OverlaySnapshotDiff(fields, Array.Empty<OverlayTeamDiff>());
        }

        var teamDiffs = DiffTeams(previous.Teams, current.Teams);
        var teamOrderChanged = !KeysEqual(previous.Teams, current.Teams, team => team.Team);
        if (teamDiffs.Count > 0 || teamOrderChanged)
        {
            fields |= OverlaySnapshotFields.Teams;
        }

        if (teamOrderChanged)
        {
            fields |= OverlaySnapshotFields.TeamOrder;
        }

        return new OverlaySnapshotDiff(fields, teamDiffs);
    }

    public static OverlaySnapshot Merge(OverlaySnapshot? previous, OverlaySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            return current;
        }

        var snapshotFields = DiffSnapshotFields(previous, current);
        var teamsVisibleEqual = TeamsVisibleEqual(previous.Teams, current.Teams);
        if (snapshotFields == OverlaySnapshotFields.None && teamsVisibleEqual)
        {
            return previous;
        }

        if (teamsVisibleEqual)
        {
            return current with { Teams = previous.Teams };
        }

        var previousTeams = ToMap(previous.Teams, team => team.Team);
        var mergedTeams = current.Teams
            .Select(team => previousTeams.TryGetValue(team.Team, out var oldTeam)
                ? MergeTeam(oldTeam, team)
                : team)
            .ToArray();
        IReadOnlyList<OverlayTeam> canonicalTeams =
            ReferenceSequenceEqual(previous.Teams, mergedTeams)
                ? previous.Teams
                : mergedTeams;
        return current with { Teams = canonicalTeams };
    }

    private static OverlaySnapshotFields DiffSnapshotFields(
        OverlaySnapshot previous,
        OverlaySnapshot current)
    {
        var fields = OverlaySnapshotFields.None;
        AddIf(previous.Phase != current.Phase, OverlaySnapshotFields.Phase, ref fields);
        AddIf(!TextEquals(previous.Header, current.Header), OverlaySnapshotFields.Header, ref fields);
        AddIf(!TextEquals(previous.Summary, current.Summary), OverlaySnapshotFields.Summary, ref fields);
        AddIf(!TextEquals(previous.ActiveRiotId, current.ActiveRiotId), OverlaySnapshotFields.ActiveRiotId, ref fields);
        AddIf(previous.ActiveTeam != current.ActiveTeam, OverlaySnapshotFields.ActiveTeam, ref fields);
        AddIf(previous.LeadingTeam != current.LeadingTeam, OverlaySnapshotFields.LeadingTeam, ref fields);
        AddIf(previous.TeamGap != current.TeamGap, OverlaySnapshotFields.TeamGap, ref fields);
        AddIf(previous.Confidence != current.Confidence, OverlaySnapshotFields.Confidence, ref fields);
        AddIf(!TextEquals(previous.StatusMessage, current.StatusMessage), OverlaySnapshotFields.StatusMessage, ref fields);
        return fields;
    }

    private static IReadOnlyList<OverlayTeamDiff> DiffTeams(
        IReadOnlyList<OverlayTeam> previous,
        IReadOnlyList<OverlayTeam> current)
    {
        var previousByKey = ToMap(previous, team => team.Team);
        var currentByKey = ToMap(current, team => team.Team);
        var diffs = new List<OverlayTeamDiff>();

        foreach (var team in current)
        {
            if (!previousByKey.TryGetValue(team.Team, out var oldTeam))
            {
                diffs.Add(AddedTeam(team));
                continue;
            }

            var teamFields = OverlayTeamFields.None;
            AddIf(!TextEquals(oldTeam.DisplayName, team.DisplayName), OverlayTeamFields.DisplayName, ref teamFields);
            AddIf(oldTeam.PerformanceScore != team.PerformanceScore, OverlayTeamFields.PerformanceScore, ref teamFields);
            var playerDiffs = DiffPlayers(oldTeam.Players, team.Players);
            var playerOrderChanged = !KeysEqual(oldTeam.Players, team.Players, player => player.StableKey);
            if (playerDiffs.Count > 0 || playerOrderChanged)
            {
                teamFields |= OverlayTeamFields.Players;
            }

            if (playerOrderChanged)
            {
                teamFields |= OverlayTeamFields.PlayerOrder;
            }

            if (teamFields != OverlayTeamFields.None)
            {
                diffs.Add(new OverlayTeamDiff(
                    team.Team,
                    SnapshotItemChange.Updated,
                    teamFields,
                    playerDiffs));
            }
        }

        foreach (var team in previous)
        {
            if (!currentByKey.ContainsKey(team.Team))
            {
                diffs.Add(new OverlayTeamDiff(
                    team.Team,
                    SnapshotItemChange.Removed,
                    OverlayTeamFields.All,
                    team.Players.Select(player => new OverlayPlayerDiff(
                        player.StableKey,
                        SnapshotItemChange.Removed,
                        OverlayPlayerFields.All)).ToArray()));
            }
        }

        return diffs;
    }

    private static IReadOnlyList<OverlayPlayerDiff> DiffPlayers(
        IReadOnlyList<OverlayPlayer> previous,
        IReadOnlyList<OverlayPlayer> current)
    {
        var previousByKey = ToMap(previous, player => player.StableKey);
        var currentByKey = ToMap(current, player => player.StableKey);
        var diffs = new List<OverlayPlayerDiff>();

        foreach (var player in current)
        {
            if (!previousByKey.TryGetValue(player.StableKey, out var oldPlayer))
            {
                diffs.Add(new OverlayPlayerDiff(
                    player.StableKey,
                    SnapshotItemChange.Added,
                    OverlayPlayerFields.All));
                continue;
            }

            var fields = DiffPlayerFields(oldPlayer, player);
            if (fields != OverlayPlayerFields.None)
            {
                diffs.Add(new OverlayPlayerDiff(
                    player.StableKey,
                    SnapshotItemChange.Updated,
                    fields));
            }
        }

        foreach (var player in previous)
        {
            if (!currentByKey.ContainsKey(player.StableKey))
            {
                diffs.Add(new OverlayPlayerDiff(
                    player.StableKey,
                    SnapshotItemChange.Removed,
                    OverlayPlayerFields.All));
            }
        }

        return diffs;
    }

    private static OverlayPlayerFields DiffPlayerFields(
        OverlayPlayer previous,
        OverlayPlayer current)
    {
        var fields = OverlayPlayerFields.None;
        AddIf(!TextEquals(previous.DisplayName, current.DisplayName), OverlayPlayerFields.DisplayName, ref fields);
        AddIf(previous.Team != current.Team, OverlayPlayerFields.Team, ref fields);
        AddIf(!TextEquals(previous.ChampionName, current.ChampionName), OverlayPlayerFields.ChampionName, ref fields);
        AddIf(!TextEquals(previous.ChampionIconPath, current.ChampionIconPath), OverlayPlayerFields.ChampionIconPath, ref fields);
        AddIf(previous.IsAnonymous != current.IsAnonymous, OverlayPlayerFields.IsAnonymous, ref fields);
        AddIf(previous.PerformanceScore != current.PerformanceScore, OverlayPlayerFields.PerformanceScore, ref fields);
        AddIf(!TextEquals(previous.PerformanceLabel, current.PerformanceLabel), OverlayPlayerFields.PerformanceLabel, ref fields);
        AddIf(previous.Confidence != current.Confidence, OverlayPlayerFields.Confidence, ref fields);
        return fields;
    }

    private static bool TeamsVisibleEqual(
        IReadOnlyList<OverlayTeam> previous,
        IReadOnlyList<OverlayTeam> current)
    {
        if (previous.Count != current.Count)
        {
            return false;
        }

        for (var teamIndex = 0; teamIndex < previous.Count; teamIndex++)
        {
            var oldTeam = previous[teamIndex];
            var newTeam = current[teamIndex];
            if (oldTeam.Team != newTeam.Team ||
                !TextEquals(oldTeam.DisplayName, newTeam.DisplayName) ||
                oldTeam.PerformanceScore != newTeam.PerformanceScore ||
                oldTeam.Players.Count != newTeam.Players.Count)
            {
                return false;
            }

            for (var playerIndex = 0; playerIndex < oldTeam.Players.Count; playerIndex++)
            {
                var oldPlayer = oldTeam.Players[playerIndex];
                var newPlayer = newTeam.Players[playerIndex];
                if (!TextEquals(oldPlayer.StableKey, newPlayer.StableKey) ||
                    DiffPlayerFields(oldPlayer, newPlayer) != OverlayPlayerFields.None)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static OverlayTeam MergeTeam(OverlayTeam previous, OverlayTeam current)
    {
        var previousPlayers = ToMap(previous.Players, player => player.StableKey);
        var mergedPlayers = current.Players
            .Select(player => previousPlayers.TryGetValue(player.StableKey, out var oldPlayer) &&
                              DiffPlayerFields(oldPlayer, player) == OverlayPlayerFields.None
                ? oldPlayer
                : player)
            .ToArray();
        IReadOnlyList<OverlayPlayer> canonicalPlayers =
            ReferenceSequenceEqual(previous.Players, mergedPlayers)
                ? previous.Players
                : mergedPlayers;
        if (TextEquals(previous.DisplayName, current.DisplayName) &&
            previous.PerformanceScore == current.PerformanceScore &&
            ReferenceEquals(previous.Players, canonicalPlayers))
        {
            return previous;
        }

        return current with { Players = canonicalPlayers };
    }

    private static OverlayTeamDiff AddedTeam(OverlayTeam team) =>
        new(
            team.Team,
            SnapshotItemChange.Added,
            OverlayTeamFields.All,
            team.Players.Select(player => new OverlayPlayerDiff(
                player.StableKey,
                SnapshotItemChange.Added,
                OverlayPlayerFields.All)).ToArray());

    private static Dictionary<TKey, TValue> ToMap<TValue, TKey>(
        IEnumerable<TValue> items,
        Func<TValue, TKey> keySelector)
        where TKey : notnull
    {
        var map = new Dictionary<TKey, TValue>();
        foreach (var item in items)
        {
            map[keySelector(item)] = item;
        }

        return map;
    }

    private static bool KeysEqual<TValue, TKey>(
        IReadOnlyList<TValue> left,
        IReadOnlyList<TValue> right,
        Func<TValue, TKey> keySelector)
        where TKey : notnull
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!EqualityComparer<TKey>.Default.Equals(
                    keySelector(left[index]),
                    keySelector(right[index])))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReferenceSequenceEqual<T>(
        IReadOnlyList<T> previous,
        IReadOnlyList<T> current)
        where T : class
    {
        if (previous.Count != current.Count)
        {
            return false;
        }

        for (var index = 0; index < previous.Count; index++)
        {
            if (!ReferenceEquals(previous[index], current[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TextEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static void AddIf(
        bool condition,
        OverlaySnapshotFields field,
        ref OverlaySnapshotFields fields)
    {
        if (condition)
        {
            fields |= field;
        }
    }

    private static void AddIf(
        bool condition,
        OverlayTeamFields field,
        ref OverlayTeamFields fields)
    {
        if (condition)
        {
            fields |= field;
        }
    }

    private static void AddIf(
        bool condition,
        OverlayPlayerFields field,
        ref OverlayPlayerFields fields)
    {
        if (condition)
        {
            fields |= field;
        }
    }
}

public sealed record OverlayUpdate(OverlaySnapshot Snapshot, OverlaySnapshotDiff Diff);

/// <summary>
/// Retains the latest canonical visible snapshot and emits at most one update per configured interval.
/// Call <see cref="Flush"/> from the adapter's timer to deliver a pending change when no new frame arrives.
/// </summary>
public sealed class OverlayUpdateReducer
{
    private readonly object _gate = new();
    private readonly TimeSpan _minimumInterval;
    private readonly TimeProvider _timeProvider;
    private OverlaySnapshot? _lastPresented;
    private OverlaySnapshot? _latest;
    private long _lastPresentedAtTimestamp;

    public OverlayUpdateReducer(
        TimeSpan minimumInterval,
        TimeProvider? timeProvider = null)
    {
        if (minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        }

        _minimumInterval = minimumInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public OverlayUpdate? Offer(OverlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            var previousLatest = _latest;
            _latest = VisibleSnapshot.Merge(previousLatest, snapshot);
            if (_lastPresented is null)
            {
                return Present(_latest);
            }

            if (ReferenceEquals(_lastPresented, _latest))
            {
                return null;
            }

            if (!ReferenceEquals(previousLatest, _lastPresented) &&
                VisibleSnapshot.VisibleEquals(_lastPresented, _latest))
            {
                _latest = _lastPresented;
                return null;
            }

            return IsDue() ? Present(_latest) : null;
        }
    }

    public OverlayUpdate? Flush()
    {
        lock (_gate)
        {
            if (_lastPresented is null || _latest is null ||
                ReferenceEquals(_lastPresented, _latest) || !IsDue())
            {
                return null;
            }

            return Present(_latest);
        }
    }

    public TimeSpan DelayUntilFlush
    {
        get
        {
            lock (_gate)
            {
                if (_lastPresented is null || _latest is null ||
                    ReferenceEquals(_lastPresented, _latest))
                {
                    return Timeout.InfiniteTimeSpan;
                }

                var remaining = _minimumInterval -
                                _timeProvider.GetElapsedTime(_lastPresentedAtTimestamp);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    private bool IsDue()
    {
        var elapsed = _timeProvider.GetElapsedTime(_lastPresentedAtTimestamp);
        return elapsed >= _minimumInterval;
    }

    private OverlayUpdate Present(OverlaySnapshot snapshot)
    {
        var diff = VisibleSnapshot.Diff(_lastPresented, snapshot);
        _lastPresented = snapshot;
        _latest = snapshot;
        _lastPresentedAtTimestamp = _timeProvider.GetTimestamp();
        return new OverlayUpdate(snapshot, diff);
    }
}
