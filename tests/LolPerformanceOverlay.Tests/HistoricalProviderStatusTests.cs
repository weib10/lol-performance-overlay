using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class HistoricalProviderStatusTests
{
    public static IEnumerable<object[]> FailureScenarios()
    {
        yield return [SyntheticHistoricalScenario.Offline, HistoricalProfileAvailability.Offline, HistoricalFailureReason.NetworkOffline];
        yield return [SyntheticHistoricalScenario.Unavailable, HistoricalProfileAvailability.Unavailable, HistoricalFailureReason.ProviderUnavailable];
        yield return [SyntheticHistoricalScenario.PolicyDisabled, HistoricalProfileAvailability.PolicyDisabled, HistoricalFailureReason.PolicyNotApproved];
        yield return [SyntheticHistoricalScenario.NotFound, HistoricalProfileAvailability.NotFound, HistoricalFailureReason.RecordNotFound];
        yield return [SyntheticHistoricalScenario.RateLimited, HistoricalProfileAvailability.RateLimited, HistoricalFailureReason.RequestThrottled];
        yield return [SyntheticHistoricalScenario.ServerError, HistoricalProfileAvailability.ServerError, HistoricalFailureReason.UpstreamFailure];
        yield return [SyntheticHistoricalScenario.Timeout, HistoricalProfileAvailability.Timeout, HistoricalFailureReason.RequestTimedOut];
        yield return [SyntheticHistoricalScenario.Malformed, HistoricalProfileAvailability.Malformed, HistoricalFailureReason.InvalidResponse];
    }

    [Theory]
    [MemberData(nameof(FailureScenarios))]
    public async Task SyntheticProviderRepresentsTransportFailuresWithoutFakeProfile(
        SyntheticHistoricalScenario scenario,
        HistoricalProfileAvailability expectedAvailability,
        HistoricalFailureReason expectedReason)
    {
        var player = HistoricalTestData.Player(10 + (int)scenario);
        var provider = new SyntheticHistoricalProfileProvider(new Dictionary<string, SyntheticHistoricalScenario>
        {
            [player.StableKey] = scenario
        });

        var result = await provider.GetProfilesAsync(
            [player],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(expectedAvailability, entry.Availability);
        Assert.Equal(expectedReason, entry.FailureReason);
        Assert.Null(entry.Profile);
    }

    [Fact]
    public async Task NotFoundRepresents404WithoutExposingTransportDetails()
    {
        var player = HistoricalTestData.Player(30);
        var provider = WithScenario(player, SyntheticHistoricalScenario.NotFound);

        var entry = Assert.Single((await provider.GetProfilesAsync(
            [player],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None)).Entries);

        Assert.Equal(HistoricalProfileAvailability.NotFound, entry.Availability);
        Assert.Equal(HistoricalFailureReason.RecordNotFound, entry.FailureReason);
    }

    [Fact]
    public async Task RateLimitedRepresents429WithoutRetryingOrBlockingLiveOverlay()
    {
        var player = HistoricalTestData.Player(31);
        var provider = WithScenario(player, SyntheticHistoricalScenario.RateLimited);

        var entry = Assert.Single((await provider.GetProfilesAsync(
            [player],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None)).Entries);

        Assert.Equal(HistoricalProfileAvailability.RateLimited, entry.Availability);
        Assert.Equal(HistoricalFailureReason.RequestThrottled, entry.FailureReason);
    }

    [Fact]
    public async Task ServerErrorRepresents5xxWithoutLeakingResponseBody()
    {
        var player = HistoricalTestData.Player(32);
        var provider = WithScenario(player, SyntheticHistoricalScenario.ServerError);

        var entry = Assert.Single((await provider.GetProfilesAsync(
            [player],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None)).Entries);

        Assert.Equal(HistoricalProfileAvailability.ServerError, entry.Availability);
        Assert.Equal(HistoricalFailureReason.UpstreamFailure, entry.FailureReason);
        Assert.Null(entry.Profile);
    }

    [Fact]
    public async Task PartialAndStaleKeepSourceSampleTimeAndConfidenceVisible()
    {
        var partial = HistoricalTestData.Player(33);
        var stale = HistoricalTestData.Player(34);
        var provider = new SyntheticHistoricalProfileProvider(new Dictionary<string, SyntheticHistoricalScenario>
        {
            [partial.StableKey] = SyntheticHistoricalScenario.Partial,
            [stale.StableKey] = SyntheticHistoricalScenario.Stale
        });

        var result = await provider.GetProfilesAsync(
            [partial, stale],
            new HistoricalProfileQuery(HistoricalQueue.RankedFlex),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Partial, result.Availability);
        Assert.Collection(
            result.Entries,
            entry =>
            {
                Assert.Equal(HistoricalProfileAvailability.Partial, entry.Availability);
                Assert.Equal(HistoricalFailureReason.IncompleteSourceData, entry.FailureReason);
                Assert.NotNull(entry.Profile);
            },
            entry =>
            {
                Assert.Equal(HistoricalProfileAvailability.Stale, entry.Availability);
                Assert.NotNull(entry.Profile);
                Assert.Equal(HistoricalSourceKind.Synthetic, entry.Profile.Source.Kind);
            });
    }

    [Fact]
    public async Task StaleProfileCanBeDisabledByCallerPolicy()
    {
        var player = HistoricalTestData.Player(35);
        var provider = WithScenario(player, SyntheticHistoricalScenario.Stale);

        var entry = Assert.Single((await provider.GetProfilesAsync(
            [player],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo, allowStale: false),
            CancellationToken.None)).Entries);

        Assert.Equal(HistoricalProfileAvailability.Unavailable, entry.Availability);
        Assert.Null(entry.Profile);
    }

    private static SyntheticHistoricalProfileProvider WithScenario(
        RevealedPlayerIdentity player,
        SyntheticHistoricalScenario scenario) =>
        new(new Dictionary<string, SyntheticHistoricalScenario>
        {
            [player.StableKey] = scenario
        });
}
