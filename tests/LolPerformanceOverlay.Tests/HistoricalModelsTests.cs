using System.Reflection;
using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class HistoricalModelsTests
{
    [Fact]
    public void HistoricalProfileRejectsUnboundedNestedCollections()
    {
        var champions = Enumerable.Range(0, 33)
            .Select(index => new HistoricalChampionUsage($"Synthetic Champion {index}", 1));
        var roles = Enumerable.Range(0, 9)
            .Select(index => new HistoricalRoleUsage($"ROLE-{index}", 1));
        var player = HistoricalTestData.Player(1);
        var now = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalTestData.Profile(
            player,
            HistoricalQueue.RankedSolo,
            now,
            commonChampions: champions));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoricalTestData.Profile(
            player,
            HistoricalQueue.RankedSolo,
            now,
            commonRoles: roles));
    }

    [Fact]
    public void RankOnlyProfileHasNoPlayStyleInsteadOfAnInventedBand()
    {
        // A rank-only source is one ranked-entries lookup with no match history behind it.
        // There is nothing to derive a style band from, so the model must allow leaving
        // PlayStyle out rather than forcing a fabricated "balanced" reading.
        var now = new DateTimeOffset(2026, 8, 16, 8, 0, 0, TimeSpan.Zero);
        var profile = new HistoricalProfile(
            HistoricalQueue.RankedSolo,
            new OfficialRank(HistoricalQueue.RankedSolo, "GOLD", "III", 42),
            sampleCount: 0,
            fetchedAt: now,
            HistoricalConfidence.InsufficientSample,
            Array.Empty<HistoricalChampionUsage>(),
            Array.Empty<HistoricalRoleUsage>(),
            playStyle: null,
            new HistoricalProfileSource(HistoricalSourceKind.LiveBackend, "官方牌位查詢"));

        Assert.Null(profile.PlayStyle);
        Assert.NotNull(profile.OfficialRank);
        Assert.Empty(profile.CommonChampions);
    }

    [Fact]
    public void RevealedIdentityRejectsOversizedProviderFields()
    {
        Assert.False(RevealedPlayerIdentity.TryCreateNormallyRevealed(
            new string('s', 257),
            "Synthetic Player",
            "TW2",
            "TW2",
            out _));
    }

    [Fact]
    public void AnonymousOrIncompleteIdentityCannotEnterHistoryBoundary()
    {
        Assert.False(RevealedPlayerIdentity.TryCreateNormallyRevealed(
            "synthetic-slot-01",
            null,
            null,
            "tw2",
            out _));
        Assert.Throws<ArgumentException>(() => RevealedPlayerIdentity.CreateNormallyRevealed(
            "synthetic-slot-01",
            "",
            "FAKE",
            "tw2"));
    }

    [Fact]
    public async Task SyntheticProfileIsQueueSpecificExplainableAndClearlyLabeled()
    {
        var now = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);
        var provider = new SyntheticHistoricalProfileProvider(timeProvider: new HistoricalManualTimeProvider(now));
        var player = HistoricalTestData.Player(1);

        var result = await provider.GetProfilesAsync(
            [player],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        var profile = Assert.Single(result.Entries).Profile!;
        Assert.Equal(HistoricalProfileAvailability.Available, result.Availability);
        Assert.Equal(420, profile.Queue.QueueId);
        Assert.Equal("CLASSIC", profile.Queue.Mode);
        Assert.NotNull(profile.OfficialRank);
        Assert.Equal(24, profile.SampleCount);
        Assert.Equal(now, profile.FetchedAt);
        Assert.Equal(HistoricalConfidence.High, profile.Confidence);
        Assert.NotEmpty(profile.CommonChampions);
        Assert.NotEmpty(profile.CommonRoles);
        Assert.NotNull(profile.PlayStyle);
        var style = profile.PlayStyle!;
        Assert.False(string.IsNullOrWhiteSpace(style.Aggression.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(style.Survival.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(style.TeamParticipation.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(style.Farming.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(style.ChampionPool.Explanation));
        Assert.Equal(HistoricalSourceKind.Synthetic, profile.Source.Kind);
        Assert.Contains("合成", profile.Source.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SmallSampleIsReportedAsInsufficientInsteadOfOverstated()
    {
        var player = HistoricalTestData.Player(2);
        var provider = new SyntheticHistoricalProfileProvider(new Dictionary<string, SyntheticHistoricalScenario>
        {
            [player.StableKey] = SyntheticHistoricalScenario.LowSample
        });

        var result = await provider.GetProfilesAsync(
            [player],
            new HistoricalProfileQuery(HistoricalQueue.Aram),
            CancellationToken.None);

        var profile = Assert.Single(result.Entries).Profile!;
        Assert.Equal(3, profile.SampleCount);
        Assert.Equal(HistoricalConfidence.InsufficientSample, profile.Confidence);
        Assert.Equal(450, profile.Queue.QueueId);
        Assert.Null(profile.OfficialRank);
    }

    [Fact]
    public async Task SyntheticProfilesAreDeterministicForTheSameIdentityAndClock()
    {
        var now = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);
        var provider = new SyntheticHistoricalProfileProvider(timeProvider: new HistoricalManualTimeProvider(now));
        var player = HistoricalTestData.Player(3);
        var query = new HistoricalProfileQuery(HistoricalQueue.RankedFlex);

        var first = Assert.Single((await provider.GetProfilesAsync([player], query, CancellationToken.None)).Entries).Profile!;
        var second = Assert.Single((await provider.GetProfilesAsync([player], query, CancellationToken.None)).Entries).Profile!;

        Assert.Equal(first.OfficialRank, second.OfficialRank);
        Assert.Equal(first.SampleCount, second.SampleCount);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.CommonChampions, second.CommonChampions);
        Assert.Equal(first.CommonRoles, second.CommonRoles);
        Assert.Equal(first.PlayStyle, second.PlayStyle);
    }

    [Fact]
    public async Task ShippingDefaultNeverSelectsSyntheticHistory()
    {
        var provider = HistoricalProfileProviders.CreateShippingDefault();

        var result = await provider.GetProfilesAsync(
            [HistoricalTestData.Player(4)],
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.IsType<PolicyDisabledHistoricalProfileProvider>(provider);
        Assert.Equal(HistoricalProfileAvailability.PolicyDisabled, result.Availability);
        Assert.All(result.Entries, entry => Assert.Null(entry.Profile));
    }

    [Fact]
    public void HistoricalDisplayModelsContainNoAlternativeRankingOrPredictionField()
    {
        var forbidden = new[]
        {
            "Mmr",
            "Elo",
            "TrueRank",
            "SkillScore",
            "Strength",
            "WinProbability",
            "TargetInstruction"
        };
        var modelTypes = new[]
        {
            typeof(HistoricalProfile),
            typeof(HistoricalProfilesResult),
            typeof(HistoricalProfileEntry),
            typeof(HistoricalPlayStyle),
            typeof(HistoricalStyleDimension),
            typeof(OfficialRank)
        };
        var names = modelTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(names, name =>
            forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void OpGgActionIsFixedHostNavigationAndDoesNotReadBrowserData()
    {
        var player = RevealedPlayerIdentity.CreateNormallyRevealed(
            "synthetic-link-01",
            "Synthetic Player / One",
            "FAKE 01",
            "tw2");

        Assert.True(OpGgProfileLinkBuilder.TryBuild(player, out var action));
        Assert.Equal("https", action.Destination.Scheme);
        Assert.Equal("op.gg", action.Destination.Host);
        Assert.StartsWith("/lol/summoners/tw/", action.Destination.AbsolutePath, StringComparison.Ordinal);
        Assert.False(action.ReadsDataBack);
    }

    [Fact]
    public void OpGgActionRejectsUnknownRegionInsteadOfChangingHostOrRoute()
    {
        var player = RevealedPlayerIdentity.CreateNormallyRevealed(
            "synthetic-link-02",
            "Synthetic Player Two",
            "FAKE02",
            "invalid-region");

        Assert.False(OpGgProfileLinkBuilder.TryBuild(player, out _));
    }
}
