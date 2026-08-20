using System.Net;
using System.Net.Http;
using LolPerformanceOverlay.Core;
using Xunit;

namespace LolPerformanceOverlay.Tests;

public sealed class RiotHistoricalProfileTransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CurrentQueueRankIsPreferredOverSoloAndFlexWhenAllAreAvailable()
    {
        // The response lists Flex before Solo on purpose: Array order must not matter, only
        // preference order does, and the query below is for Solo -- its own ladder -- so Solo
        // must win even though it appears second in the response.
        var handler = new FakeRiotHandler(request =>
        {
            if (request.RequestUri!.Host == "asia.api.riotgames.com")
            {
                Assert.Equal(
                    "/riot/account/v1/accounts/by-riot-id/Synthetic%20Solo/TW1",
                    request.RequestUri.AbsolutePath);
                return Json(200, """{"puuid":"puuid-abc"}""");
            }

            Assert.Equal("tw2.api.riotgames.com", request.RequestUri.Host);
            Assert.Equal("/lol/league/v4/entries/by-puuid/puuid-abc", request.RequestUri.AbsolutePath);
            return Json(200, """
                [
                  {"queueType":"RANKED_FLEX_SR","tier":"GOLD","rank":"I","leaguePoints":10},
                  {"queueType":"RANKED_SOLO_5x5","tier":"DIAMOND","rank":"III","leaguePoints":42}
                ]
                """);
        });

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("Solo", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Available, result.Availability);
        var profile = result.Profile!;
        Assert.NotNull(profile.OfficialRank);
        Assert.Equal("DIAMOND", profile.OfficialRank!.Tier);
        Assert.Equal("III", profile.OfficialRank.Division);
        Assert.Equal(42, profile.OfficialRank.LeaguePoints);
        Assert.Equal(HistoricalQueue.RankedSolo.QueueId, profile.OfficialRank.Queue.QueueId);
        Assert.Null(profile.PlayStyle);
        Assert.Equal(0, profile.SampleCount);
        Assert.Equal(HistoricalConfidence.InsufficientSample, profile.Confidence);
        Assert.Equal(HistoricalSourceKind.LiveBackend, profile.Source.Kind);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task CurrentQueueRankIsPreferredEvenWhenTheGenericSoloFirstOrderWouldPickTheOtherOne()
    {
        // The current queue is Flex here, so Flex must win over Solo even though Solo comes
        // first in the generic (current-less) fallback order -- proving "current queue" beats
        // "Solo first", not just that Solo happens to win because it usually is the current one.
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, """
                [
                  {"queueType":"RANKED_SOLO_5x5","tier":"GOLD","rank":"I","leaguePoints":10},
                  {"queueType":"RANKED_FLEX_SR","tier":"DIAMOND","rank":"III","leaguePoints":42}
                ]
                """));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedFlex),
            CancellationToken.None);

        var profile = result.Profile!;
        Assert.Equal("DIAMOND", profile.OfficialRank!.Tier);
        Assert.Equal(HistoricalQueue.RankedFlex.QueueId, profile.OfficialRank.Queue.QueueId);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SoloRankIsUsedAsFallbackWhenTheCurrentQueueHasNoEntry()
    {
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, """[{"queueType":"RANKED_SOLO_5x5","tier":"GOLD","rank":"I","leaguePoints":15}]"""));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedFlex),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Available, result.Availability);
        var profile = result.Profile!;
        Assert.Equal("GOLD", profile.OfficialRank!.Tier);
        // The rank is honestly labelled with the queue it actually came from, not the queue
        // that was queried -- profile.Queue stays Flex (the game being played) while the rank
        // itself carries Solo.
        Assert.Equal(HistoricalQueue.RankedFlex.QueueId, profile.Queue.QueueId);
        Assert.Equal(HistoricalQueue.RankedSolo.QueueId, profile.OfficialRank.Queue.QueueId);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task FlexRankIsUsedAsFallbackWhenNeitherTheCurrentQueueNorSoloHasAnEntry()
    {
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, """[{"queueType":"RANKED_FLEX_SR","tier":"GOLD","rank":"I","leaguePoints":0}]"""));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Available, result.Availability);
        var profile = result.Profile!;
        Assert.Equal(HistoricalQueue.RankedSolo.QueueId, profile.Queue.QueueId);
        Assert.Equal(HistoricalQueue.RankedFlex.QueueId, profile.OfficialRank!.Queue.QueueId);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ANoLadderQueueFallsBackToTheSoloRankInsteadOfFailing()
    {
        // ARAM has no ladder of its own, so the preference order simply starts at Solo -- and
        // now actually makes the two HTTP calls instead of short-circuiting before either.
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, """[{"queueType":"RANKED_SOLO_5x5","tier":"PLATINUM","rank":"II","leaguePoints":33}]"""));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.Aram),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Available, result.Availability);
        var profile = result.Profile!;
        Assert.Equal(HistoricalQueue.Aram.QueueId, profile.Queue.QueueId);
        Assert.Equal("PLATINUM", profile.OfficialRank!.Tier);
        Assert.Equal(HistoricalQueue.RankedSolo.QueueId, profile.OfficialRank.Queue.QueueId);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ANoLadderQueueFallsBackToTheFlexRankWhenNoSoloEntryExists()
    {
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, """[{"queueType":"RANKED_FLEX_SR","tier":"SILVER","rank":"IV","leaguePoints":5}]"""));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.Aram),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Available, result.Availability);
        var profile = result.Profile!;
        Assert.Equal(HistoricalQueue.Aram.QueueId, profile.Queue.QueueId);
        Assert.Equal("SILVER", profile.OfficialRank!.Tier);
        Assert.Equal(HistoricalQueue.RankedFlex.QueueId, profile.OfficialRank.Queue.QueueId);
        // Worst case for the "only one league-entries request" guarantee: this player has
        // three misses (own queue has no ladder, then Solo) before the Flex hit, and it must
        // still be exactly the one response already fetched, not a third HTTP call.
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task AResolvedAccountWithNoEntryAnywhereInThePreferenceOrderIsAnAvailableUnrankedProfileNotAFailure()
    {
        // ACCOUNT-V1 found this exact player -- the account genuinely exists -- and LEAGUE-V4
        // answered with their entries; there just are none. That is "unranked in Solo and
        // Flex", a normal fact about the player, not a failed lookup: it must come back as an
        // Available profile with a null OfficialRank (which OfficialRankAttachment.Describe
        // renders as "未" / 尚未定位), never as NotFound/RecordNotFound -- that failure marker
        // is reserved for when the account itself cannot be resolved (see
        // RiotIdNotFoundStopsBeforeAnyLeagueLookup right below this test).
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, "[]"));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Available, result.Availability);
        var profile = result.Profile!;
        Assert.Null(profile.OfficialRank);
        Assert.Equal(HistoricalQueue.RankedSolo.QueueId, profile.Queue.QueueId);
        // A rank-only lookup with nothing behind it -- honest, not invented, filler.
        Assert.Equal(0, profile.SampleCount);
        Assert.Equal(HistoricalConfidence.InsufficientSample, profile.Confidence);
        Assert.Null(profile.PlayStyle);
        Assert.Empty(profile.CommonChampions);
        Assert.Empty(profile.CommonRoles);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task AResolvedAccountWithOnlyUnrelatedQueueEntriesIsAlsoAnAvailableUnrankedProfile()
    {
        // Same fact as an empty array, reached a different way: the account has ranked
        // entries, just none that FindPreferredEntry's preference order (this queue's own
        // ladder, then Solo, then Flex) ever matches. TFT's double-up queue is a real Riot
        // queueType this endpoint can report that is neither Solo nor Flex.
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, """[{"queueType":"RANKED_TFT_DOUBLE_UP","tier":"GOLD","rank":"I","leaguePoints":10}]"""));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Available, result.Availability);
        Assert.Null(result.Profile!.OfficialRank);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ApiKeyTravelsOnlyInTheHeaderNeverInTheUrl()
    {
        var handler = new FakeRiotHandler(request =>
        {
            Assert.True(request.Headers.TryGetValues("X-Riot-Token", out var values));
            Assert.Equal("secret-test-key", Assert.Single(values!));
            Assert.DoesNotContain("secret-test-key", request.RequestUri!.ToString(), StringComparison.Ordinal);
            return request.RequestUri.Host == "asia.api.riotgames.com"
                ? Json(200, """{"puuid":"p"}""")
                : Json(200, "[]");
        });

        using var transport = CreateTransport(handler, key: "secret-test-key");
        await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);
    }

    [Fact]
    public async Task RiotIdNotFoundStopsBeforeAnyLeagueLookup()
    {
        // The only remaining path to NotFound/RecordNotFound: ACCOUNT-V1 itself could not
        // resolve this Riot ID, so there is no account to have ranked entries at all -- a
        // genuinely different claim from "the account resolved but has no Solo/Flex rank"
        // (see AResolvedAccountWithNoEntryAnywhereInThePreferenceOrderIsAnAvailableUnrankedProfileNotAFailure
        // above), which is why this one stops before the league lookup even starts.
        var handler = new FakeRiotHandler(_ => Json(404, "{}"));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("Ghost", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.NotFound, result.Availability);
        Assert.Equal(HistoricalFailureReason.RecordNotFound, result.FailureReason);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task BadOrExpiredKeyIsReportedAsUnavailableNotACrash(HttpStatusCode status)
    {
        var handler = new FakeRiotHandler(_ => new HttpResponseMessage(status));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Unavailable, result.Availability);
        Assert.Equal(HistoricalFailureReason.ProviderUnavailable, result.FailureReason);
    }

    [Fact]
    public async Task RateLimitBacksOffForTheServerSpecifiedDurationWithoutRetrying()
    {
        var handler = new FakeRiotHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        var clock = new HistoricalManualTimeProvider(Now);

        using var transport = CreateTransport(handler, timeProvider: clock);
        var first = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);
        Assert.Equal(HistoricalProfileAvailability.RateLimited, first.Availability);
        Assert.Equal(1, handler.CallCount);

        // Still inside the 30s window: must short-circuit without a second HTTP call.
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);
        Assert.Equal(HistoricalProfileAvailability.RateLimited, second.Availability);
        Assert.Equal(1, handler.CallCount);

        // Past the window: the real two-call request (account-v1, then league-v4) resumes and
        // succeeds, proving the backoff cleared rather than merely "isn't RateLimited any more".
        clock.Advance(TimeSpan.FromSeconds(25));
        handler.NextResponse = request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, """[{"queueType":"RANKED_SOLO_5x5","tier":"GOLD","rank":"I","leaguePoints":5}]""");
        var third = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);
        Assert.Equal(HistoricalProfileAvailability.Available, third.Availability);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task ServerErrorIsReportedWithoutLeakingResponseBody()
    {
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.ServerError, result.Availability);
        Assert.Equal(HistoricalFailureReason.UpstreamFailure, result.FailureReason);
    }

    [Fact]
    public async Task MalformedJsonIsReportedAsMalformedNotAnUnhandledException()
    {
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, "{ not valid json"));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Malformed, result.Availability);
        Assert.Equal(HistoricalFailureReason.InvalidResponse, result.FailureReason);
    }

    [Fact]
    public async Task MissingTierOrRankFieldIsMalformedRatherThanAFabricatedRank()
    {
        var handler = new FakeRiotHandler(request => request.RequestUri!.Host == "asia.api.riotgames.com"
            ? Json(200, """{"puuid":"p"}""")
            : Json(200, """[{"queueType":"RANKED_SOLO_5x5","tier":"","rank":"","leaguePoints":0}]"""));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Malformed, result.Availability);
    }

    [Fact]
    public async Task UnresolvableRegionShortCircuitsWithoutAnyHttpCall()
    {
        var handler = new FakeRiotHandler(_ => throw new InvalidOperationException(
            "An unmapped region must fail before any request is built."));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "not-a-real-platform"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Unavailable, result.Availability);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task NetworkFailureIsReportedAsOfflineNotAnUnhandledException()
    {
        var handler = new FakeRiotHandler(_ => throw new HttpRequestException("connection reset"));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Offline, result.Availability);
        Assert.Equal(HistoricalFailureReason.NetworkOffline, result.FailureReason);
    }

    [Fact]
    public async Task TransportTimeoutIsReportedAsTimeoutWhenTheCallerDidNotCancel()
    {
        var handler = new FakeRiotHandler(_ => throw new TaskCanceledException("client timeout"));

        using var transport = CreateTransport(handler);
        var result = await transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            CancellationToken.None);

        Assert.Equal(HistoricalProfileAvailability.Timeout, result.Availability);
        Assert.Equal(HistoricalFailureReason.RequestTimedOut, result.FailureReason);
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfBeingReportedAsTimeout()
    {
        var handler = new FakeRiotHandler(_ => throw new TaskCanceledException("client timeout"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        using var transport = CreateTransport(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.FetchAsync(
            Player("A", "TW1", "tw2"),
            new HistoricalProfileQuery(HistoricalQueue.RankedSolo),
            cancellation.Token));
    }

    private static RevealedPlayerIdentity Player(string gameName, string tagLine, string region) =>
        HistoricalTestData.Player(gameName, tagLine, region);

    private static RiotHistoricalProfileTransport CreateTransport(
        FakeRiotHandler handler,
        string key = "test-key",
        TimeProvider? timeProvider = null) =>
        new(key, timeProvider, new HttpClient(handler));

    private static HttpResponseMessage Json(int statusCode, string body) => new((HttpStatusCode)statusCode)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class FakeRiotHandler : HttpMessageHandler
    {
        public FakeRiotHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => NextResponse = respond;

        public Func<HttpRequestMessage, HttpResponseMessage> NextResponse { get; set; }
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(NextResponse(request));
        }
    }
}
