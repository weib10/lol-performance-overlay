using LolPerformanceOverlay.Core;

namespace LolPerformanceOverlay.Tests;

internal static class HistoricalTestData
{
    public static RevealedPlayerIdentity Player(int number) =>
        RevealedPlayerIdentity.CreateNormallyRevealed(
            $"synthetic-stable-{number:D2}",
            $"Synthetic Player {number:D2}",
            $"FAKE{number:D2}",
            "tw2");

    public static HistoricalProfile Profile(
        RevealedPlayerIdentity player,
        HistoricalQueue queue,
        DateTimeOffset fetchedAt,
        int sampleCount = 20) =>
        new(
            queue,
            queue.QueueId is 420 or 440 ? new OfficialRank(queue, "SILVER", "II", 42) : null,
            sampleCount,
            fetchedAt,
            sampleCount < 5 ? HistoricalConfidence.InsufficientSample : HistoricalConfidence.High,
            [new HistoricalChampionUsage("Synthetic Champion Alpha", Math.Min(8, sampleCount))],
            [new HistoricalRoleUsage("MIDDLE", sampleCount)],
            new HistoricalPlayStyle(
                new HistoricalStyleDimension(HistoricalStyleBand.Balanced, "合成交戰傾向"),
                new HistoricalStyleDimension(HistoricalStyleBand.High, "合成存活傾向"),
                new HistoricalStyleDimension(HistoricalStyleBand.Balanced, "合成團隊參與傾向"),
                new HistoricalStyleDimension(HistoricalStyleBand.Low, "合成發育傾向"),
                new HistoricalStyleDimension(HistoricalStyleBand.Balanced, "合成英雄池廣度")),
            new HistoricalProfileSource(HistoricalSourceKind.LiveBackend, "合成 transport fixture"));
}

internal sealed class HistoricalManualTimeProvider : TimeProvider
{
    public HistoricalManualTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; private set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan elapsed) => UtcNow += elapsed;
}
