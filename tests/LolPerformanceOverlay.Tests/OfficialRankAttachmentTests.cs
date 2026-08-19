using LolPerformanceOverlay.Core;
using LolPerformanceOverlay.Core.Presentation;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class OfficialRankAttachmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TenAvailableProfilesGiveAllTenPlayersAShortCode()
    {
        var snapshot = TenPlayerSnapshot();
        var profiles = ProfilesResult(Enumerable.Range(1, 10).Select(number => AvailableEntry(number, "GOLD", "IV")));

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);

        var players = attached.Teams.SelectMany(team => team.Players).ToArray();
        Assert.Equal(10, players.Length);
        Assert.All(players, player => Assert.False(string.IsNullOrEmpty(player.OfficialRank?.ShortCode)));
    }

    [Fact]
    public void JoinIsByStableKeyNotDisplayText()
    {
        var snapshot = TenPlayerSnapshot();
        // HistoricalTestData.Player(1) resolves to game name "Synthetic Player 01" / tag
        // "FAKE01" -- deliberately different from the "Synthetic Row 01#FAKE01" DisplayName
        // the matching OverlayPlayer carries below, so a match here can only have happened
        // through StableKey, never a name comparison.
        var profiles = ProfilesResult([AvailableEntry(1, "DIAMOND", "IV")]);

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);

        var player = attached.Teams
            .SelectMany(team => team.Players)
            .Single(candidate => candidate.StableKey == HistoricalTestData.Player(1).StableKey);
        Assert.Equal("D4", player.OfficialRank?.ShortCode);
    }

    [Fact]
    public void NoProfilesReturnsTheSameSnapshotInstance()
    {
        var snapshot = TenPlayerSnapshot();

        Assert.Same(snapshot, OfficialRankAttachment.Attach(snapshot, null));
        Assert.Same(snapshot, OfficialRankAttachment.Attach(snapshot, ProfilesResult([])));
    }

    [Fact]
    public void EntryForAStableKeyNotInTheSnapshotIsIgnoredWithoutThrowing()
    {
        var snapshot = TenPlayerSnapshot();
        // Player 11 was never part of the ten-player roster built by TenPlayerSnapshot.
        var profiles = ProfilesResult([AvailableEntry(11, "GOLD", "IV")]);

        var exception = Record.Exception(() => OfficialRankAttachment.Attach(snapshot, profiles));
        Assert.Null(exception);

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);
        Assert.Same(snapshot, attached);
        Assert.All(attached.Teams.SelectMany(team => team.Players), player => Assert.Null(player.OfficialRank));
    }

    [Fact]
    public void AttachingTheSameProfilesTwiceProducesNoDiffOrReducerUpdate()
    {
        var profiles = ProfilesResult(Enumerable.Range(1, 10).Select(number => AvailableEntry(number, "GOLD", "IV")));
        var first = OfficialRankAttachment.Attach(TenPlayerSnapshot(Now), profiles);
        var second = OfficialRankAttachment.Attach(TenPlayerSnapshot(Now.AddSeconds(1)), profiles);

        var diff = VisibleSnapshot.Diff(first, second);
        Assert.False(diff.HasChanges);

        var reducer = new OverlayUpdateReducer(TimeSpan.Zero);
        Assert.NotNull(reducer.Offer(first));
        Assert.Null(reducer.Offer(second));
    }

    [Fact]
    public void RankChangeFlagsOnlyTheRankFieldNotScoreChampionOrIcon()
    {
        var before = OfficialRankAttachment.Attach(TenPlayerSnapshot(), ProfilesResult([AvailableEntry(1, "GOLD", "IV")]));
        var after = OfficialRankAttachment.Attach(TenPlayerSnapshot(), ProfilesResult([AvailableEntry(1, "DIAMOND", "II")]));

        var diff = VisibleSnapshot.Diff(before, after);

        var teamDiff = Assert.Single(diff.Teams);
        var playerDiff = Assert.Single(teamDiff.Players);
        Assert.Equal(HistoricalTestData.Player(1).StableKey, playerDiff.StableKey);
        Assert.Equal(OverlayPlayerFields.OfficialRank, playerDiff.Fields);
    }

    [Fact]
    public void RosterChangeStopsAStaleEntryFromAttachingToTheNewOccupant()
    {
        var oldRoster = SingleOccupantSnapshot(playerNumber: 1);
        var profiles = ProfilesResult([AvailableEntry(1, "DIAMOND", "IV")]);
        var attachedOld = OfficialRankAttachment.Attach(oldRoster, profiles);
        Assert.NotNull(attachedOld.Teams[0].Players[0].OfficialRank);

        // A different player now occupies the same seat after a roster change; the profiles
        // result still only describes the departed player 1.
        var newRoster = SingleOccupantSnapshot(playerNumber: 2);

        var attachedNew = OfficialRankAttachment.Attach(newRoster, profiles);

        Assert.Same(newRoster, attachedNew);
        Assert.Null(attachedNew.Teams[0].Players[0].OfficialRank);
    }

    public static IEnumerable<object[]> FailureAvailabilities()
    {
        yield return [HistoricalProfileAvailability.Offline, HistoricalFailureReason.NetworkOffline];
        yield return [HistoricalProfileAvailability.PolicyDisabled, HistoricalFailureReason.PolicyNotApproved];
        yield return [HistoricalProfileAvailability.NotFound, HistoricalFailureReason.RecordNotFound];
        yield return [HistoricalProfileAvailability.RateLimited, HistoricalFailureReason.RequestThrottled];
        yield return [HistoricalProfileAvailability.ServerError, HistoricalFailureReason.UpstreamFailure];
        yield return [HistoricalProfileAvailability.Timeout, HistoricalFailureReason.RequestTimedOut];
        yield return [HistoricalProfileAvailability.Malformed, HistoricalFailureReason.InvalidResponse];
        yield return [HistoricalProfileAvailability.Unavailable, HistoricalFailureReason.ProviderUnavailable];
    }

    [Theory]
    [MemberData(nameof(FailureAvailabilities))]
    public void EveryLookupFailureGetsAPlainLanguageMarkerAndSentenceInsteadOfABlankCell(
        HistoricalProfileAvailability availability,
        HistoricalFailureReason reason)
    {
        var snapshot = SingleOccupantSnapshot(playerNumber: 1);
        var profiles = ProfilesResult([HistoricalProfileEntry.Failure(HistoricalTestData.Player(1), availability, reason)]);

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);

        var display = attached.Teams[0].Players[0].OfficialRank;
        Assert.NotNull(display);
        // Every failure collapses to the same neutral cell marker -- ten rows of a distinct,
        // obscure glyph reads as the app being broken, and these failures are almost always
        // roster-wide. The specific reason survives in StatusText for #9's tooltip instead.
        Assert.Equal("—", display!.ShortCode);
        Assert.False(string.IsNullOrWhiteSpace(display.StatusText));
        // A failure entry never carries cached data, so it is never marked stale -- staleness
        // only means anything when there is an old profile behind the marker.
        Assert.False(display.IsStale);
    }

    [Fact]
    public void StatusTextNeverLeaksDeveloperJargon()
    {
        var forbidden = new[] { "PUUID", "LEAGUE-V4", "RATE LIMIT", "TRANSPORT" };
        var allStatusText = AllReachableDisplays().Select(display => display.StatusText);

        foreach (var text in allStatusText)
        {
            foreach (var term in forbidden)
            {
                Assert.DoesNotContain(term, text.ToUpperInvariant());
            }
        }
    }

    [Fact]
    public void EveryStateHasItsOwnSentenceEvenWhereTheCellMarkerIsSharedOnPurpose()
    {
        var displays = AllReachableDisplays().ToArray();

        // StatusText is where every state must stay distinguishable (it is what #9's tooltip
        // will show) -- only the fresh-rank state has an empty one (the code already says
        // everything), so a plain Distinct() count over all of them, including that one empty
        // string, should still equal the number of states.
        Assert.Equal(displays.Length, displays.Select(display => display.StatusText).Distinct().Count());
    }

    [Fact]
    public void TheCellMarkerItselfCollapsesToASmallFixedSetNotOneGlyphPerState()
    {
        // The whole point of AGENTS.md rule 6 (白話) here: the 25px cell must never grow a new
        // distinct glyph every time a new failure reason is added. Every reachable display's
        // ShortCode must be one of exactly these -- a real rank code (with or without the stale
        // suffix), unranked (with or without the stale suffix), the neutral failure marker, or
        // empty (no ladder in this queue).
        var displays = AllReachableDisplays().ToArray();
        var allowedShortCodes = new HashSet<string>(StringComparer.Ordinal) { "D4", "D4*", "未", "未*", "—", "" };

        Assert.All(displays, display => Assert.Contains(display.ShortCode, allowedShortCodes));
        // And the failure marker specifically must be shared by more than one state -- if this
        // ever collapses to 1, the "collapse many failures into one glyph" design point is gone
        // and the test above would no longer be exercising it.
        Assert.True(displays.Count(display => display.ShortCode == "—") > 1);
    }

    [Fact]
    public void UnrankedInARankedQueueIsADifferentSentenceFromRecordNotFound()
    {
        var unranked = AttachSingle(NoRankEntry(1, HistoricalQueue.RankedSolo));
        var notFound = AttachSingle(FailureEntry(2, HistoricalProfileAvailability.NotFound, HistoricalFailureReason.RecordNotFound));

        Assert.NotEqual(unranked.ShortCode, notFound.ShortCode);
        Assert.NotEqual(unranked.StatusText, notFound.StatusText);
        Assert.Contains("未定位", unranked.StatusText);
    }

    [Fact]
    public void NoLadderQueueDoesNotClaimThePlayerIsUnplaced()
    {
        // ARAM has no ranked ladder at all -- this is exactly what the built-in replay fixture
        // produces for it (Available profile, OfficialRank null, Queue.QueueId 450). Saying
        // "unranked" here would falsely imply a ladder exists for this player to be unplaced on.
        var noLadder = AttachSingle(NoRankEntry(1, HistoricalQueue.Aram));
        var unranked = AttachSingle(NoRankEntry(2, HistoricalQueue.RankedSolo));

        Assert.NotEqual(unranked.ShortCode, noLadder.ShortCode);
        Assert.DoesNotContain("未定位", noLadder.StatusText);
        // Empty on purpose -- see OfficialRankDisplay's doc comment and
        // OverlayWindow.UpdatePlayerRank, which collapses the cell whenever the text is empty:
        // the concept has no value to show in a ladderless queue, on any player, ever, so a
        // marker here would be clutter repeated on every row of every ARAM game.
        Assert.Equal(string.Empty, noLadder.ShortCode);
        Assert.False(noLadder.IsStale);
    }

    [Fact]
    public void NoRankedLadderFailureReasonMatchesTheProfilePresentNoLadderDisplayExactly()
    {
        // The live transport reaches "no ladder" as a failure (Profile null, FailureReason
        // NoRankedLadder -- see RiotHistoricalProfileTransport.FetchAsync), while the
        // synthetic/replay fixture reaches the very same real-world fact with a Profile
        // present and OfficialRank null (see NoRankEntry/HistoricalTestData.Profile). Both
        // must land on an identical display -- not just an equally-empty ShortCode, but the
        // exact same StatusText too -- otherwise a player would see different wording for the
        // same fact depending on which code path happened to produce it.
        var viaProfile = AttachSingle(NoRankEntry(1, HistoricalQueue.Aram));
        var viaFailure = AttachSingle(FailureEntry(
            2,
            HistoricalProfileAvailability.Unavailable,
            HistoricalFailureReason.NoRankedLadder));

        Assert.Equal(viaProfile, viaFailure);
        Assert.Equal(string.Empty, viaFailure.ShortCode);
        Assert.NotEqual(string.Empty, viaFailure.StatusText);
    }

    [Fact]
    public void NoRankedLadderFailureReasonIsNotLumpedInWithGenericUnavailable()
    {
        // Both share HistoricalProfileAvailability.Unavailable -- the honest availability is
        // the same in both cases, there is truly nothing to serve -- but the reason is what
        // must not lie. A broken provider (ProviderUnavailable, e.g. an unmapped region) still
        // gets the generic failure marker and "temporarily busy" wording; a queue with no
        // ladder at all must not, because nothing is actually broken.
        var noLadder = AttachSingle(FailureEntry(
            1,
            HistoricalProfileAvailability.Unavailable,
            HistoricalFailureReason.NoRankedLadder));
        var genericUnavailable = AttachSingle(FailureEntry(
            2,
            HistoricalProfileAvailability.Unavailable,
            HistoricalFailureReason.ProviderUnavailable));

        Assert.Equal(string.Empty, noLadder.ShortCode);
        Assert.Equal("—", genericUnavailable.ShortCode);
        Assert.NotEqual(noLadder.StatusText, genericUnavailable.StatusText);
        Assert.DoesNotContain("忙碌", noLadder.StatusText);
        Assert.DoesNotContain("故障", noLadder.StatusText);
    }

    [Fact]
    public void StaleRankIsMarkedDifferentlyFromAFreshOneOfTheSameTierAndDivision()
    {
        var fresh = AttachSingle(AvailableEntry(1, "DIAMOND", "IV"));
        var stale = AttachSingle(StaleEntry(2, "DIAMOND", "IV"));

        Assert.Equal("D4", fresh.ShortCode);
        Assert.False(fresh.IsStale);
        Assert.NotEqual(fresh.ShortCode, stale.ShortCode);
        Assert.True(stale.IsStale);
        Assert.StartsWith("D4", stale.ShortCode);
        Assert.NotEqual(string.Empty, stale.StatusText);
    }

    [Fact]
    public void StaleUnrankedCarriesTheStaleMarkerButStaleNoLadderStaysCollapsed()
    {
        var staleUnranked = AttachSingle(NoRankEntry(1, HistoricalQueue.RankedSolo, HistoricalProfileAvailability.Stale));
        var staleNoLadder = AttachSingle(NoRankEntry(2, HistoricalQueue.Aram, HistoricalProfileAvailability.Stale));

        Assert.True(staleUnranked.IsStale);
        Assert.Equal("未*", staleUnranked.ShortCode);
        // IsStale is still recorded honestly on the no-ladder display (for #9's tooltip), but
        // the cell itself stays empty even when stale: there is no value here to be old, so
        // there is nothing for the "*" to be attached to -- a lone "*" floating in an otherwise
        // empty row would be more confusing than informative.
        Assert.True(staleNoLadder.IsStale);
        Assert.Equal(string.Empty, staleNoLadder.ShortCode);
        Assert.NotEqual(staleUnranked.ShortCode, staleNoLadder.ShortCode);
    }

    [Fact]
    public void RankOnlySourceWithNoPlayStyleSampleShowsOnlyTheRankAndNoStyleWording()
    {
        var identity = HistoricalTestData.Player(1);
        // sampleCount: 0 and includePlayStyle: false together model the exact "only a ranked
        // entries lookup, no match history" source the live transport produces -- see
        // RiotHistoricalProfileTransport.FetchAsync, which never sets PlayStyle at all.
        var profile = HistoricalTestData.Profile(
            identity,
            HistoricalQueue.RankedSolo,
            Now,
            sampleCount: 0,
            includePlayStyle: false);
        Assert.Null(profile.PlayStyle);
        Assert.NotNull(profile.OfficialRank);

        var entry = HistoricalProfileEntry.WithProfile(identity, HistoricalProfileAvailability.Available, profile);
        var display = AttachSingle(entry);

        Assert.Equal("S2", display.ShortCode);
        var styleWords = new[] { "平衡", "很低", "偏低", "偏高", "很高", "Balanced", "風格" };
        foreach (var word in styleWords)
        {
            Assert.DoesNotContain(word, display.StatusText);
        }
    }

    [Fact]
    public void RosterWidePolicyDisabledLeavesEveryOtherSnapshotFieldUntouched()
    {
        var snapshot = TenPlayerSnapshot();
        var profiles = ProfilesResult(Enumerable.Range(1, 10)
            .Select(number => FailureEntry(number, HistoricalProfileAvailability.PolicyDisabled, HistoricalFailureReason.PolicyNotApproved)));

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);

        Assert.All(
            attached.Teams.SelectMany(team => team.Players),
            player => Assert.Equal("—", player.OfficialRank?.ShortCode));
        Assert.All(
            attached.Teams.SelectMany(team => team.Players),
            player => Assert.Equal("還沒有設定官方牌位查詢功能", player.OfficialRank?.StatusText));
        // Everything except OfficialRank on each player is untouched -- policy-disabled must
        // never affect the rest of the snapshot the core overlay depends on.
        Assert.Equal(snapshot.Phase, attached.Phase);
        Assert.Equal(snapshot.Header, attached.Header);
        Assert.Equal(snapshot.Summary, attached.Summary);
        Assert.Equal(snapshot.ActiveRiotId, attached.ActiveRiotId);
        Assert.Equal(snapshot.ActiveTeam, attached.ActiveTeam);
        Assert.Equal(snapshot.LeadingTeam, attached.LeadingTeam);
        Assert.Equal(snapshot.TeamGap, attached.TeamGap);
        Assert.Equal(snapshot.Confidence, attached.Confidence);
        Assert.Equal(snapshot.StatusMessage, attached.StatusMessage);
        for (var teamIndex = 0; teamIndex < snapshot.Teams.Count; teamIndex++)
        {
            Assert.Equal(snapshot.Teams[teamIndex].DisplayName, attached.Teams[teamIndex].DisplayName);
            Assert.Equal(snapshot.Teams[teamIndex].PerformanceScore, attached.Teams[teamIndex].PerformanceScore);
            for (var playerIndex = 0; playerIndex < snapshot.Teams[teamIndex].Players.Count; playerIndex++)
            {
                var before = snapshot.Teams[teamIndex].Players[playerIndex];
                var after = attached.Teams[teamIndex].Players[playerIndex];
                Assert.Equal(before.DisplayName, after.DisplayName);
                Assert.Equal(before.ChampionName, after.ChampionName);
                Assert.Equal(before.PerformanceScore, after.PerformanceScore);
                Assert.Equal(before.PerformanceLabel, after.PerformanceLabel);
            }
        }
    }

    [Fact]
    public void AttachingTheSameMixOfRankedUnrankedNoLadderAndFailedProfilesTwiceProducesNoDiffOrReducerUpdate()
    {
        var profiles = ProfilesResult(
        [
            AvailableEntry(1, "GOLD", "IV"),
            NoRankEntry(2, HistoricalQueue.RankedSolo),
            NoRankEntry(3, HistoricalQueue.Aram),
            FailureEntry(4, HistoricalProfileAvailability.NotFound, HistoricalFailureReason.RecordNotFound),
            FailureEntry(5, HistoricalProfileAvailability.RateLimited, HistoricalFailureReason.RequestThrottled),
            StaleEntry(6, "SILVER", "II"),
            FailureEntry(7, HistoricalProfileAvailability.Unavailable, HistoricalFailureReason.NoRankedLadder)
        ]);
        var first = OfficialRankAttachment.Attach(TenPlayerSnapshot(Now), profiles);
        var second = OfficialRankAttachment.Attach(TenPlayerSnapshot(Now.AddSeconds(1)), profiles);

        var diff = VisibleSnapshot.Diff(first, second);
        Assert.False(diff.HasChanges);

        var reducer = new OverlayUpdateReducer(TimeSpan.Zero);
        Assert.NotNull(reducer.Offer(first));
        Assert.Null(reducer.Offer(second));
    }

    // Issue #9: OfficialRankDisplay.TooltipText is the composed row-tooltip block Core hands
    // the WPF adapter verbatim (see OverlayWindow.UpdateRowTooltip). Everything below asserts
    // its content directly, the same way the ShortCode/StatusText tests above do.

    [Fact]
    public void TooltipTextIncludesFullTierNameAndLeaguePoints()
    {
        var display = AttachSingle(AvailableEntry(1, "DIAMOND", "IV"));

        Assert.Contains("鑽石 IV", display.TooltipText);
        Assert.Contains("42 LP", display.TooltipText);
    }

    [Fact]
    public void TooltipTextOmitsLeaguePointsWhenRiotDidNotReportThem()
    {
        var display = AttachSingle(AvailableEntryWithoutLeaguePoints(1, "GOLD", "III"));

        Assert.Contains("金 III", display.TooltipText);
        Assert.DoesNotContain("LP", display.TooltipText);
    }

    [Fact]
    public void TooltipTextSkipsDivisionWordingForApexTiersWithNoMeaningfulDivision()
    {
        // Riot's API always reports "I" as the division for the three apex tiers -- it does
        // not mean anything the way I-IV do for everyone else, so it must not appear.
        var display = AttachSingle(AvailableEntry(1, "MASTER", "I"));

        Assert.Contains("大師 · 42 LP", display.TooltipText);
        Assert.DoesNotContain("大師 I", display.TooltipText);
    }

    [Fact]
    public void TooltipTextIncludesQueueSourceDisplayNameAndFetchTime()
    {
        var display = AttachSingle(AvailableEntry(1, "DIAMOND", "IV"));

        Assert.Contains(HistoricalQueue.RankedSolo.DisplayName, display.TooltipText);
        Assert.Contains("合成測試資料", display.TooltipText);
        Assert.Contains(Now.ToLocalTime().ToString("MM/dd HH:mm"), display.TooltipText);
    }

    [Fact]
    public void StaleRankTooltipStatesStalenessInWordsNotJustTheAsteriskSuffix()
    {
        var display = AttachSingle(StaleEntry(1, "DIAMOND", "IV"));

        Assert.Contains("*", display.ShortCode);
        Assert.Contains("較舊", display.TooltipText);
    }

    [Fact]
    public void TooltipTextAttributesTheRankToRiotAndTheScoreToThisGame()
    {
        var display = AttachSingle(AvailableEntry(1, "DIAMOND", "IV"));

        Assert.Contains("Riot", display.TooltipText);
        Assert.Contains("本場相對表現", display.TooltipText);
    }

    [Fact]
    public void RankOnlyProfileWithNoPlayStyleSampleProducesATooltipWithNoStyleWordingAtAll()
    {
        // HistoricalProfile.PlayStyle null (a single ranked-entries lookup, no match history --
        // exactly what RiotHistoricalProfileTransport produces) is a legitimate rank-only
        // source, not a gap to paper over. The tooltip must never invent a style reading for it.
        var identity = HistoricalTestData.Player(1);
        var rankOnlyProfile = HistoricalTestData.Profile(
            identity,
            HistoricalQueue.RankedSolo,
            Now,
            sampleCount: 0,
            includePlayStyle: false);
        Assert.Null(rankOnlyProfile.PlayStyle);
        var display = AttachSingle(
            HistoricalProfileEntry.WithProfile(identity, HistoricalProfileAvailability.Available, rankOnlyProfile));

        var styleWords = new[]
        {
            "平衡", "很低", "偏低", "偏高", "很高", "Balanced",
            "風格", "激進", "生存", "團隊", "發育", "英雄池"
        };
        Assert.All(styleWords, word => Assert.DoesNotContain(word, display.TooltipText));
    }

    [Fact]
    public void TooltipTextNeverMentionsPlayStyleEvenWhenTheProfileHasOne()
    {
        // The tooltip this ticket adds only ever covers rank -- style is out of scope for it --
        // so the same "no style wording" guarantee must hold even when a profile has a style
        // to read, not just when PlayStyle happens to be null.
        var identity = HistoricalTestData.Player(1);
        var profileWithStyle = HistoricalTestData.Profile(identity, HistoricalQueue.RankedSolo, Now);
        Assert.NotNull(profileWithStyle.PlayStyle);
        var display = AttachSingle(
            HistoricalProfileEntry.WithProfile(identity, HistoricalProfileAvailability.Available, profileWithStyle));

        var styleWords = new[]
        {
            "平衡", "很低", "偏低", "偏高", "很高", "Balanced",
            "風格", "激進", "生存", "團隊", "發育", "英雄池"
        };
        Assert.All(styleWords, word => Assert.DoesNotContain(word, display.TooltipText));
    }

    [Fact]
    public void NoTooltipOrStatusTextEverMentionsMmrEloOrWinRateWording()
    {
        var forbidden = new[] { "MMR", "ELO", "勝率", "WIN RATE", "WINRATE" };
        var displays = AllReachableDisplays().Append(AttachSingle(AvailableEntry(1, "DIAMOND", "IV")));

        foreach (var display in displays)
        {
            foreach (var term in forbidden)
            {
                Assert.DoesNotContain(term, display.TooltipText.ToUpperInvariant());
                Assert.DoesNotContain(term, display.StatusText.ToUpperInvariant());
            }
        }
    }

    [Fact]
    public void EveryReachableDisplayHasANonEmptyTooltipTextOnceALookupResultExists()
    {
        // A row is never blank on hover once a lookup completed for it -- unranked, no-ladder
        // and every failure each get a tooltip explaining why, not just a resolved rank.
        Assert.All(AllReachableDisplays(), display => Assert.False(string.IsNullOrWhiteSpace(display.TooltipText)));
    }

    [Fact]
    public void TooltipTextIsDeterministicForIdenticalProfileInput()
    {
        var first = AttachSingle(AvailableEntry(1, "DIAMOND", "IV"));
        var second = AttachSingle(AvailableEntry(1, "DIAMOND", "IV"));

        Assert.Equal(first.TooltipText, second.TooltipText);
    }

    // Builds the snapshot's lone occupant from the entry's own identity, rather than a
    // separately-passed player number, so a caller can never accidentally mismatch StableKeys
    // between the entry it is attaching and the row it expects the result on.
    private static OfficialRankDisplay AttachSingle(HistoricalProfileEntry entry)
    {
        var occupant = new OverlayPlayer(
            entry.Identity.StableKey,
            "測試玩家",
            100,
            "Ashe",
            null,
            false,
            50,
            "持平",
            PerformanceConfidence.High);
        var snapshot = new OverlaySnapshot(
            LeaguePhase.InGame,
            Now,
            "本場即時表現",
            "雙方接近",
            null,
            null,
            null,
            null,
            PerformanceConfidence.High,
            [new OverlayTeam(100, "藍方", 50, [occupant])]);

        var attached = OfficialRankAttachment.Attach(snapshot, ProfilesResult([entry]));
        return attached.Teams[0].Players[0].OfficialRank!;
    }

    /// <summary>
    /// One display per state this ticket must make distinguishable: a fresh rank, a stale
    /// rank, unranked, no-ladder-queue (as reached via the synthetic/replay fixture's
    /// profile-present shape -- see <see cref="NoRankedLadderFailureReasonMatchesTheProfilePresentNoLadderDisplayExactly"/>
    /// below for the live transport's failure-shaped path onto the very same display), and
    /// every lookup failure. Backs the no-jargon and marker-set assertions above so they cover
    /// the whole surface area in one place instead of drifting out of sync with it.
    /// </summary>
    private static IEnumerable<OfficialRankDisplay> AllReachableDisplays()
    {
        yield return AttachSingle(AvailableEntry(1, "DIAMOND", "IV"));
        yield return AttachSingle(StaleEntry(1, "DIAMOND", "IV"));
        yield return AttachSingle(NoRankEntry(1, HistoricalQueue.RankedSolo));
        yield return AttachSingle(NoRankEntry(1, HistoricalQueue.Aram));
        foreach (var scenario in FailureAvailabilities())
        {
            yield return AttachSingle(FailureEntry(
                1,
                (HistoricalProfileAvailability)scenario[0],
                (HistoricalFailureReason)scenario[1]));
        }
    }

    private static HistoricalProfileEntry FailureEntry(
        int number,
        HistoricalProfileAvailability availability,
        HistoricalFailureReason reason) =>
        HistoricalProfileEntry.Failure(HistoricalTestData.Player(number), availability, reason);

    private static HistoricalProfileEntry NoRankEntry(
        int number,
        HistoricalQueue queue,
        HistoricalProfileAvailability availability = HistoricalProfileAvailability.Available)
    {
        var identity = HistoricalTestData.Player(number);
        var profile = new HistoricalProfile(
            queue,
            null,
            sampleCount: 20,
            fetchedAt: Now,
            HistoricalConfidence.High,
            Array.Empty<HistoricalChampionUsage>(),
            Array.Empty<HistoricalRoleUsage>(),
            playStyle: null,
            new HistoricalProfileSource(HistoricalSourceKind.Synthetic, "合成測試資料"));
        return HistoricalProfileEntry.WithProfile(identity, availability, profile);
    }

    private static HistoricalProfileEntry StaleEntry(int number, string tier, string division)
    {
        var identity = HistoricalTestData.Player(number);
        var profile = new HistoricalProfile(
            HistoricalQueue.RankedSolo,
            new OfficialRank(HistoricalQueue.RankedSolo, tier, division, 20),
            sampleCount: 20,
            fetchedAt: Now - TimeSpan.FromHours(3),
            HistoricalConfidence.High,
            Array.Empty<HistoricalChampionUsage>(),
            Array.Empty<HistoricalRoleUsage>(),
            playStyle: null,
            new HistoricalProfileSource(HistoricalSourceKind.Synthetic, "合成測試資料"));
        return HistoricalProfileEntry.WithProfile(identity, HistoricalProfileAvailability.Stale, profile, HistoricalFailureReason.ProviderUnavailable);
    }

    [Fact]
    public void AnonymousPlayersNeverReceiveAnOfficialRank()
    {
        var identity = HistoricalTestData.Player(1);
        var anonymous = new OverlayPlayer(
            identity.StableKey,
            "匿名玩家",
            100,
            "尚未選擇",
            null,
            true,
            null,
            null,
            null);
        var snapshot = new OverlaySnapshot(
            LeaguePhase.InGame,
            Now,
            "本場即時表現",
            "雙方接近",
            null,
            null,
            null,
            null,
            PerformanceConfidence.High,
            [new OverlayTeam(100, "藍方", null, [anonymous])]);
        var profiles = ProfilesResult([AvailableEntry(1, "DIAMOND", "IV")]);

        var attached = OfficialRankAttachment.Attach(snapshot, profiles);

        Assert.Same(snapshot, attached);
        Assert.Null(attached.Teams[0].Players[0].OfficialRank);
    }

    private static OverlaySnapshot TenPlayerSnapshot(DateTimeOffset? capturedAt = null) =>
        new(
            LeaguePhase.InGame,
            capturedAt ?? Now,
            "本場即時表現",
            "雙方接近",
            null,
            null,
            null,
            null,
            PerformanceConfidence.High,
            [
                new OverlayTeam(100, "藍方", 50, Enumerable.Range(1, 5).Select(RowPlayer).ToArray()),
                new OverlayTeam(200, "紅方", 50, Enumerable.Range(6, 5).Select(RowPlayer).ToArray())
            ]);

    private static OverlaySnapshot SingleOccupantSnapshot(int playerNumber) =>
        new(
            LeaguePhase.InGame,
            Now,
            "本場即時表現",
            "雙方接近",
            null,
            null,
            null,
            null,
            PerformanceConfidence.High,
            [new OverlayTeam(100, "藍方", 50, [RowPlayer(playerNumber)])]);

    private static OverlayPlayer RowPlayer(int number) =>
        new(
            HistoricalTestData.Player(number).StableKey,
            $"Synthetic Row {number:D2}#FAKE{number:D2}",
            number <= 5 ? 100 : 200,
            "Ashe",
            null,
            false,
            50,
            "持平",
            PerformanceConfidence.High);

    private static HistoricalProfileEntry AvailableEntry(int number, string tier, string division)
    {
        var identity = HistoricalTestData.Player(number);
        var profile = new HistoricalProfile(
            HistoricalQueue.RankedSolo,
            new OfficialRank(HistoricalQueue.RankedSolo, tier, division, 42),
            sampleCount: 20,
            fetchedAt: Now,
            HistoricalConfidence.High,
            Array.Empty<HistoricalChampionUsage>(),
            Array.Empty<HistoricalRoleUsage>(),
            playStyle: null,
            new HistoricalProfileSource(HistoricalSourceKind.Synthetic, "合成測試資料"));
        return HistoricalProfileEntry.WithProfile(identity, HistoricalProfileAvailability.Available, profile);
    }

    // Riot does not always report LeaguePoints (OfficialRank.LeaguePoints is nullable for
    // exactly that reason); this builds the entry AvailableEntry cannot, one with no LP at all.
    private static HistoricalProfileEntry AvailableEntryWithoutLeaguePoints(int number, string tier, string division)
    {
        var identity = HistoricalTestData.Player(number);
        var profile = new HistoricalProfile(
            HistoricalQueue.RankedSolo,
            new OfficialRank(HistoricalQueue.RankedSolo, tier, division),
            sampleCount: 20,
            fetchedAt: Now,
            HistoricalConfidence.High,
            Array.Empty<HistoricalChampionUsage>(),
            Array.Empty<HistoricalRoleUsage>(),
            playStyle: null,
            new HistoricalProfileSource(HistoricalSourceKind.Synthetic, "合成測試資料"));
        return HistoricalProfileEntry.WithProfile(identity, HistoricalProfileAvailability.Available, profile);
    }

    private static HistoricalProfilesResult ProfilesResult(IEnumerable<HistoricalProfileEntry> entries) =>
        new(HistoricalProfileAvailability.Available, entries, Now);
}
