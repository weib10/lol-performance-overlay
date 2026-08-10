using System.Diagnostics;
using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Presentation;
using Xunit;
using Xunit.Abstractions;

namespace LolPerformanceOverlay.Tests;

public sealed class PipelinePerformanceTests
{
    private const int SimulatedThirtyMinuteFrameCount = 1_800;
    private readonly ITestOutputHelper _output;

    public PipelinePerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ThirtyMinutePolicyProxyAvoidsUnchangedPresentationWork()
    {
        var snapshots = Enumerable.Range(0, SimulatedThirtyMinuteFrameCount)
            .Select(index => VisibleSnapshotTests.Snapshot(
                50 + index / 60,
                DateTimeOffset.UnixEpoch.AddSeconds(index)))
            .ToArray();
        var clock = new FakeTimeProvider();
        var reducer = new OverlayUpdateReducer(TimeSpan.FromMilliseconds(250), clock);
        var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var optimizedUpdates = 0;

        foreach (var snapshot in snapshots)
        {
            if (reducer.Offer(snapshot) is not null)
            {
                optimizedUpdates++;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
        }

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
        _output.WriteLine(
            "frames={0}; legacy_updates={0}; optimized_updates={1}; reducer_elapsed_ms={2:F3}; reducer_allocated_bytes={3}",
            SimulatedThirtyMinuteFrameCount,
            optimizedUpdates,
            stopwatch.Elapsed.TotalMilliseconds,
            allocated);

        Assert.Equal(30, optimizedUpdates);
        Assert.True(allocated < 1L * 1024 * 1024);
    }

    [Fact]
    public void ScorerAndReducerDoNotRetainThirtyMinutesOfFrames()
    {
        var scorer = new PerformanceScorer();
        var reducer = new OverlayUpdateReducer(TimeSpan.FromMilliseconds(250));
        var players = Enumerable.Range(0, 10)
            .Select(index => Player(index, index < 5 ? 100 : 200))
            .ToArray();
        var oldSnapshots = new List<WeakReference<OverlaySnapshot>>();
        var before = GC.GetTotalMemory(true);
        var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < SimulatedThirtyMinuteFrameCount; index++)
        {
            var frame = new LeagueSessionFrame(
                LeaguePhase.InGame,
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                600,
                "ARAM",
                450,
                "Synthetic 0#SAFE",
                Array.Empty<ChampSelectMember>(),
                players);
            var snapshot = scorer.Evaluate(frame);
            reducer.Offer(snapshot);
            if (index < 20)
            {
                oldSnapshots.Add(new WeakReference<OverlaySnapshot>(snapshot));
            }
        }

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retainedGrowth = GC.GetTotalMemory(true) - before;
        var retainedOldSnapshots = oldSnapshots.Count(reference => reference.TryGetTarget(out _));
        _output.WriteLine(
            "frames={0}; scorer_reducer_elapsed_ms={1:F3}; allocated_bytes={2}; retained_growth_bytes={3}; retained_old_snapshots={4}",
            SimulatedThirtyMinuteFrameCount,
            stopwatch.Elapsed.TotalMilliseconds,
            allocated,
            retainedGrowth,
            retainedOldSnapshots);

        Assert.True(retainedOldSnapshots <= 1);
        Assert.True(retainedGrowth < 10L * 1024 * 1024);
        Assert.True(allocated < 16L * 1024 * 1024);
    }

    [Fact]
    [Trait("Category", "ManualSoak")]
    public async Task OptionalWallClockThirtyMinuteSoak()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("LOL_OVERLAY_RUN_30_MIN_SOAK"),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine("Set LOL_OVERLAY_RUN_30_MIN_SOAK=1 to run the 30-minute wall-clock soak.");
            return;
        }

        var scorer = new PerformanceScorer();
        var reducer = new OverlayUpdateReducer(TimeSpan.FromMilliseconds(250));
        var players = Enumerable.Range(0, 10)
            .Select(index => Player(index, index < 5 ? 100 : 200))
            .ToArray();
        var end = DateTimeOffset.UtcNow.AddMinutes(30);
        var frames = 0;
        var startMemory = GC.GetTotalMemory(true);
        while (DateTimeOffset.UtcNow < end)
        {
            var frame = new LeagueSessionFrame(
                LeaguePhase.InGame,
                DateTimeOffset.UtcNow,
                frames,
                "ARAM",
                450,
                "Synthetic 0#SAFE",
                Array.Empty<ChampSelectMember>(),
                players);
            reducer.Offer(scorer.Evaluate(frame));
            frames++;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        var growth = GC.GetTotalMemory(true) - startMemory;
        _output.WriteLine("wall_clock_frames={0}; retained_growth_bytes={1}", frames, growth);
        Assert.True(growth < 10L * 1024 * 1024);
    }

    private static RawPlayerState Player(int index, int team) =>
        new(
            $"p{index}",
            $"Synthetic {index}#SAFE",
            team,
            $"Champion{index}",
            $"Champion {index}",
            null,
            [ChampionArchetype.Fighter],
            2,
            2,
            3,
            25,
            10,
            [new RawItemState(1_000 + index, 1, 6_000)]);
}
