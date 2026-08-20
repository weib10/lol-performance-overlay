using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class OpGgMultiSearchLinkBuilderTests
{
    [Fact]
    public void FullTenPlayerRosterProducesOneLinkContainingEveryPlayerWithTheirTag()
    {
        var roster = Enumerable.Range(1, 10).Select(HistoricalTestData.Player).ToArray();

        Assert.True(OpGgProfileLinkBuilder.TryBuildMultiSearch(roster, out var action));

        Assert.Equal("https", action.Destination.Scheme);
        Assert.Equal("op.gg", action.Destination.Host);
        Assert.Equal("/zh-tw/lol/multisearch/tw", action.Destination.AbsolutePath);
        Assert.False(action.ReadsDataBack);

        var entries = DecodeSummonerEntries(action.Destination);
        Assert.Equal(10, entries.Length);
        Assert.Equal(
            roster.Select(player => $"{player.GameName}#{player.TagLine}"),
            entries);
    }

    [Fact]
    public void EachEntryIsPercentEncodedIncludingSpacesAndSymbols()
    {
        var plain = HistoricalTestData.Player(1);
        var needsEscaping = HistoricalTestData.Player("Multi Search", "FA&KE 1", "tw2");
        var roster = new[] { plain, needsEscaping };

        Assert.True(OpGgProfileLinkBuilder.TryBuildMultiSearch(roster, out var action));

        // The raw query string must carry percent-escapes for the space and '&' in the second
        // identity's game name/tag line -- a literal space or unescaped '&' would either break
        // the URL or be misread as a second query parameter.
        var rawQuery = action.Destination.Query;
        Assert.DoesNotContain(" ", rawQuery);
        Assert.Contains(Uri.EscapeDataString($"{needsEscaping.GameName}#{needsEscaping.TagLine}"), rawQuery);

        var entries = DecodeSummonerEntries(action.Destination);
        Assert.Equal(
            new[]
            {
                $"{plain.GameName}#{plain.TagLine}",
                $"{needsEscaping.GameName}#{needsEscaping.TagLine}"
            },
            entries);
    }

    [Fact]
    public void UnmappedRegionReturnsFalse()
    {
        var roster = new[] { HistoricalTestData.Player("Unknown Shard", "FAKE01", "invalid-region") };

        Assert.False(OpGgProfileLinkBuilder.TryBuildMultiSearch(roster, out _));
    }

    [Fact]
    public void DisagreeingRegionsAcrossTheRosterReturnFalse()
    {
        // A single match always shares one platform; a roster spanning two regions cannot be
        // this game's roster, and building a link for only one region's players would silently
        // drop or misresolve the rest.
        var roster = new[]
        {
            HistoricalTestData.Player("Blue Side", "FAKE01", "tw2"),
            HistoricalTestData.Player("Red Side", "FAKE02", "na1")
        };

        Assert.False(OpGgProfileLinkBuilder.TryBuildMultiSearch(roster, out _));
    }

    [Fact]
    public void EmptyRosterReturnsFalse()
    {
        Assert.False(OpGgProfileLinkBuilder.TryBuildMultiSearch(Array.Empty<RevealedPlayerIdentity>(), out _));
    }

    [Fact]
    public void DestinationPassesTheUserInitiatedBrowserNetworkPolicy()
    {
        var roster = new[] { HistoricalTestData.Player(1) };

        Assert.True(OpGgProfileLinkBuilder.TryBuildMultiSearch(roster, out var action));

        Assert.True(NetworkDestinationPolicy.IsAllowed(
            action.Destination,
            NetworkDestinationPurpose.UserInitiatedBrowser));
    }

    [Fact]
    public void RosterCapDropsPlayersBeyondTenInsteadOfGrowingTheLinkWithoutBound()
    {
        var roster = Enumerable.Range(1, 11).Select(HistoricalTestData.Player).ToArray();

        Assert.True(OpGgProfileLinkBuilder.TryBuildMultiSearch(roster, out var action));

        var entries = DecodeSummonerEntries(action.Destination);
        Assert.Equal(10, entries.Length);
        Assert.Equal(
            roster.Take(10).Select(player => $"{player.GameName}#{player.TagLine}"),
            entries);
    }

    private static string[] DecodeSummonerEntries(Uri destination)
    {
        var query = destination.Query.TrimStart('?');
        const string prefix = "summoners=";
        Assert.StartsWith(prefix, query, StringComparison.Ordinal);
        var raw = query[prefix.Length..];
        return raw
            .Split(',')
            .Select(Uri.UnescapeDataString)
            .ToArray();
    }
}
