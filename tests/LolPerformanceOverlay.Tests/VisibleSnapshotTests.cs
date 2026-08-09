using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Presentation;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class VisibleSnapshotTests
{
    [Fact]
    public void CaptureTimeAndCollectionInstancesDoNotCreateVisibleChanges()
    {
        var original = Snapshot(50, DateTimeOffset.UnixEpoch);
        var equivalent = Snapshot(50, DateTimeOffset.UnixEpoch.AddSeconds(1));

        var diff = VisibleSnapshot.Diff(original, equivalent);
        var merged = VisibleSnapshot.Merge(original, equivalent);

        Assert.False(diff.HasChanges);
        Assert.Same(original, merged);
    }

    [Fact]
    public void PlayerChangesAreReportedByStableKeyAndReuseUnchangedPlayers()
    {
        var original = Snapshot(50, DateTimeOffset.UnixEpoch);
        var changed = Snapshot(61, DateTimeOffset.UnixEpoch.AddSeconds(1));

        var diff = VisibleSnapshot.Diff(original, changed);
        var merged = VisibleSnapshot.Merge(original, changed);

        var teamDiff = Assert.Single(diff.Teams);
        var playerDiff = Assert.Single(teamDiff.Players);
        Assert.Equal("p1", playerDiff.StableKey);
        Assert.Equal(SnapshotItemChange.Updated, playerDiff.Change);
        Assert.Equal(OverlayPlayerFields.PerformanceScore, playerDiff.Fields);
        Assert.NotSame(original.Teams[0].Players[0], merged.Teams[0].Players[0]);
        Assert.Same(original.Teams[0].Players[1], merged.Teams[0].Players[1]);
    }

    [Fact]
    public void AddedAndRemovedPlayersAreExplicitKeyedChanges()
    {
        var original = Snapshot(50, DateTimeOffset.UnixEpoch);
        var replacement = Player("p3", 100, 48);
        var changed = original with
        {
            CapturedAt = original.CapturedAt.AddSeconds(1),
            Teams =
            [
                original.Teams[0] with { Players = [original.Teams[0].Players[0], replacement] }
            ]
        };

        var playerDiffs = Assert.Single(VisibleSnapshot.Diff(original, changed).Teams).Players;

        Assert.Contains(playerDiffs, item => item.StableKey == "p2" && item.Change == SnapshotItemChange.Removed);
        Assert.Contains(playerDiffs, item => item.StableKey == "p3" && item.Change == SnapshotItemChange.Added);
    }

    [Fact]
    public void PlayerReorderingIsAnExplicitStructuralChange()
    {
        var original = Snapshot(50, DateTimeOffset.UnixEpoch);
        var reordered = original with
        {
            Teams = [original.Teams[0] with { Players = original.Teams[0].Players.Reverse().ToArray() }]
        };

        var diff = VisibleSnapshot.Diff(original, reordered);
        var teamDiff = Assert.Single(diff.Teams);

        Assert.NotEqual(OverlayTeamFields.None, teamDiff.Fields & OverlayTeamFields.PlayerOrder);
        Assert.NotEqual(OverlayTeamFields.None, teamDiff.Fields & OverlayTeamFields.Players);
    }

    [Fact]
    public void TeamReorderingIsAnExplicitStructuralChange()
    {
        var original = Snapshot(50, DateTimeOffset.UnixEpoch);
        var second = new OverlayTeam(200, "紅方", 49, [Player("p3", 200, 49)]);
        var withTwoTeams = original with { Teams = [original.Teams[0], second] };
        var reordered = withTwoTeams with { Teams = [second, original.Teams[0]] };

        var diff = VisibleSnapshot.Diff(withTwoTeams, reordered);

        Assert.NotEqual(OverlaySnapshotFields.None, diff.Fields & OverlaySnapshotFields.TeamOrder);
        Assert.NotEqual(OverlaySnapshotFields.None, diff.Fields & OverlaySnapshotFields.Teams);
    }

    [Fact]
    public void ReducerSuppressesNoOpFramesAndFlushesLatestChangeAfterThrottle()
    {
        var clock = new FakeTimeProvider();
        var reducer = new OverlayUpdateReducer(TimeSpan.FromMilliseconds(500), clock);
        var first = Snapshot(50, DateTimeOffset.UnixEpoch);

        Assert.NotNull(reducer.Offer(first));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Null(reducer.Offer(Snapshot(50, DateTimeOffset.UnixEpoch.AddSeconds(1))));
        Assert.Null(reducer.Offer(Snapshot(55, DateTimeOffset.UnixEpoch.AddSeconds(2))));
        Assert.Equal(TimeSpan.FromMilliseconds(400), reducer.DelayUntilFlush);
        clock.Advance(TimeSpan.FromMilliseconds(399));
        Assert.Null(reducer.Flush());
        clock.Advance(TimeSpan.FromMilliseconds(1));

        var update = Assert.IsType<OverlayUpdate>(reducer.Flush());
        Assert.Equal(55, update.Snapshot.Teams[0].Players[0].PerformanceScore);
        Assert.True(update.Diff.HasChanges);
        Assert.Null(reducer.Flush());
        Assert.Equal(Timeout.InfiniteTimeSpan, reducer.DelayUntilFlush);
    }

    [Fact]
    public void PendingChangeThatRevertsToPresentedStateIsDiscarded()
    {
        var clock = new FakeTimeProvider();
        var reducer = new OverlayUpdateReducer(TimeSpan.FromSeconds(1), clock);
        reducer.Offer(Snapshot(50, DateTimeOffset.UnixEpoch));
        reducer.Offer(Snapshot(60, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        reducer.Offer(Snapshot(50, DateTimeOffset.UnixEpoch.AddSeconds(2)));
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(reducer.Flush());
    }

    internal static OverlaySnapshot Snapshot(double firstScore, DateTimeOffset capturedAt, string? summary = null) =>
        new(
            LeaguePhase.InGame,
            capturedAt,
            "本場即時表現",
            summary ?? "雙方接近",
            "Synthetic Player#SAFE",
            100,
            null,
            0,
            PerformanceConfidence.High,
            [
                new OverlayTeam(
                    100,
                    "藍方",
                    50,
                    [Player("p1", 100, firstScore), Player("p2", 100, 50)])
            ]);

    private static OverlayPlayer Player(string key, int team, double score) =>
        new(
            key,
            $"Synthetic {key}",
            team,
            "Ashe",
            null,
            false,
            score,
            "持平",
            PerformanceConfidence.High);
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan elapsed)
    {
        _utcNow += elapsed;
        _timestamp += elapsed.Ticks;
    }
}
