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

    // The release gate only allows direct RevealedPlayerIdentity construction inside this
    // audited file (eng/package-config.json's syntheticIdentityFactoryFiles). Tests that need
    // a specific game name, tag line, or region -- e.g. to exercise region routing -- call this
    // instead of constructing directly themselves.
    public static RevealedPlayerIdentity Player(string gameName, string tagLine, string region) =>
        RevealedPlayerIdentity.CreateNormallyRevealed(
            $"synthetic-stable-{gameName}-{tagLine}",
            $"Synthetic {gameName}",
            tagLine,
            region);

    public static HistoricalProfile Profile(
        RevealedPlayerIdentity player,
        HistoricalQueue queue,
        DateTimeOffset fetchedAt,
        int sampleCount = 20,
        IEnumerable<HistoricalChampionUsage>? commonChampions = null,
        IEnumerable<HistoricalRoleUsage>? commonRoles = null,
        bool includePlayStyle = true) =>
        new(
            queue,
            queue.QueueId is 420 or 440 ? new OfficialRank(queue, "SILVER", "II", 42) : null,
            sampleCount,
            fetchedAt,
            sampleCount < 5 ? HistoricalConfidence.InsufficientSample : HistoricalConfidence.High,
            commonChampions ?? [new HistoricalChampionUsage("Synthetic Champion Alpha", Math.Min(8, sampleCount))],
            commonRoles ?? [new HistoricalRoleUsage("MIDDLE", sampleCount)],
            includePlayStyle
                ? new HistoricalPlayStyle(
                    new HistoricalStyleDimension(HistoricalStyleBand.Balanced, "合成交戰傾向"),
                    new HistoricalStyleDimension(HistoricalStyleBand.High, "合成存活傾向"),
                    new HistoricalStyleDimension(HistoricalStyleBand.Balanced, "合成團隊參與傾向"),
                    new HistoricalStyleDimension(HistoricalStyleBand.Low, "合成發育傾向"),
                    new HistoricalStyleDimension(HistoricalStyleBand.Balanced, "合成英雄池廣度"))
                : null,
            new HistoricalProfileSource(HistoricalSourceKind.LiveBackend, "合成 transport fixture"));
}

internal sealed class HistoricalManualTimeProvider : TimeProvider
{
    public HistoricalManualTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; private set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan elapsed) => UtcNow += elapsed;
}
