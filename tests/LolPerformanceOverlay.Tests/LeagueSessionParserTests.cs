using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class LeagueSessionParserTests
{
    [Fact]
    public void HiddenPuuidIsNeverReturnedFromChampSelectParser()
    {
        const string json = """
            {
              "myTeam": [
                {
                  "cellId": 0,
                  "team": 1,
                  "championId": 22,
                  "nameVisibilityType": "VISIBLE",
                  "puuid": "visible-puuid"
                }
              ],
              "theirTeam": [
                {
                  "cellId": 5,
                  "team": 2,
                  "championPickIntent": 99,
                  "nameVisibilityType": "HIDDEN",
                  "puuid": "",
                  "obfuscatedPuuid": "must-not-leak"
                }
              ]
            }
            """;

        var result = LeagueSessionParser.ParseChampSelectMembers(json);

        Assert.Equal("visible-puuid", result[0].Puuid);
        Assert.False(result[0].IsAnonymous);
        Assert.Null(result[1].Puuid);
        Assert.True(result[1].IsAnonymous);
        Assert.Equal(200, result[1].Team);
    }

    [Fact]
    public void PickOrderFollowsTheActionSequenceAndIgnoresBans()
    {
        const string json = """
            {
              "myTeam": [
                { "cellId": 0, "team": 1, "championId": 22, "nameVisibilityType": "VISIBLE", "puuid": "a" },
                { "cellId": 1, "team": 1, "championId": 64, "nameVisibilityType": "VISIBLE", "puuid": "b" }
              ],
              "theirTeam": [
                { "cellId": 5, "team": 2, "championId": 99, "nameVisibilityType": "VISIBLE", "puuid": "c" }
              ],
              "actions": [
                [ { "actorCellId": 5, "type": "ban", "completed": true },
                  { "actorCellId": 0, "type": "ban", "completed": true } ],
                [ { "actorCellId": 5, "type": "pick", "completed": true } ],
                [ { "actorCellId": 1, "type": "pick", "completed": true },
                  { "actorCellId": 0, "type": "pick", "completed": false } ]
              ]
            }
            """;

        var result = LeagueSessionParser.ParseChampSelectMembers(json);

        // Bans do not consume a pick number, so the enemy cell that picked first is 1.
        Assert.Equal(3, result.Single(member => member.StableKey == "cell-0-100").PickOrder);
        Assert.Equal(2, result.Single(member => member.StableKey == "cell-1-100").PickOrder);
        Assert.Equal(1, result.Single(member => member.StableKey == "cell-5-200").PickOrder);
    }

    [Fact]
    public void PickOrderIsAbsentWhenTheClientReportsNoActions()
    {
        const string json = """
            {
              "myTeam": [
                { "cellId": 0, "team": 1, "championId": 22, "nameVisibilityType": "VISIBLE", "puuid": "a" }
              ],
              "theirTeam": []
            }
            """;

        var result = LeagueSessionParser.ParseChampSelectMembers(json);

        Assert.Null(result.Single().PickOrder);
    }

    [Fact]
    public void LivePlayerParserAcceptsRiotFieldCasing()
    {
        const string json = """
            [
              {
                "riotId": "測試玩家02#TEST",
                "team": "ORDER",
                "championName": "Khazix",
                "level": 8,
                "scores": {
                  "kills": 7,
                  "deaths": 2,
                  "assists": 3,
                  "creepScore": 19
                },
                "items": [
                  { "itemID": 6692, "count": 1, "price": 3000 }
                ]
              }
            ]
            """;

        var result = LeagueSessionParser.ParseLivePlayers(json);

        var player = Assert.Single(result);
        Assert.Equal("測試玩家02#TEST", player.RiotId);
        Assert.Equal(100, player.Team);
        Assert.Equal(7, player.Kills);
        Assert.Equal(6692, Assert.Single(player.Items).ItemId);
    }

    [Fact]
    public void MissingLiveFieldsDegradeToSafeDefaults()
    {
        const string json = """[{ "team": "CHAOS", "championName": "Unknown" }]""";

        var player = Assert.Single(LeagueSessionParser.ParseLivePlayers(json));

        Assert.Equal(200, player.Team);
        Assert.Equal(0, player.Kills);
        Assert.Empty(player.Items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("UNKNOWN_SCHEMA_VALUE")]
    [InlineData("HIDDEN")]
    public void ChampSelectIdentityFailsClosedUnlessVisibilityIsExplicitlyVisible(string? visibility)
    {
        var visibilityProperty = visibility is null
            ? string.Empty
            : $"\"nameVisibilityType\":\"{visibility}\",";
        var json = $$"""
            {
              "myTeam": [{
                "cellId": 0,
                "team": 1,
                {{visibilityProperty}}
                "puuid": "must-remain-private"
              }]
            }
            """;

        var player = Assert.Single(LeagueSessionParser.ParseChampSelectMembers(json));

        Assert.True(player.IsAnonymous);
        Assert.Null(player.Puuid);
    }

    [Fact]
    public void LivePayloadIsCappedToGameRosterAndInventoryCardinality()
    {
        var item = "{\"itemID\":1001,\"count\":1,\"price\":300}";
        var players = Enumerable.Range(0, 25).Select(index => $$"""
            {
              "riotId":"Synthetic {{index}}#SAFE",
              "team":"ORDER",
              "items":[{{string.Join(',', Enumerable.Repeat(item, 20))}}]
            }
            """);
        var parsed = LeagueSessionParser.ParseLivePlayers($"[{string.Join(',', players)}]");

        Assert.Equal(10, parsed.Count);
        Assert.All(parsed, player => Assert.Equal(7, player.Items.Count));
    }
}
