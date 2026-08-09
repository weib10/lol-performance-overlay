# Historical player data: policy and architecture research

Research date: 2026-08-10 (Asia/Taipei)

Scope: options for adding historical League of Legends performance and play-style information to this public Windows overlay. This is an engineering/policy assessment, not legal advice. Only first-party Riot and OP.GG sources are cited. Where the sources do not settle a question, this document says so explicitly.

## Executive conclusion

Historical data is technically available, but it should **not** be added by putting a Riot API key in the distributed EXE or by silently scraping OP.GG.

The defensible long-term source is Riot's official API, using a registered product, an approved production key, and a small backend that keeps the key secret. Riot explicitly says that API keys must not be included in code—especially a distributed binary—and that development/personal keys cannot run a public product. Development keys also deactivate every 24 hours. A public desktop-only implementation therefore cannot safely share one Riot key without a backend. ([Riot LoL policy](https://developer.riotgames.com/docs/lol), [Riot Developer Portal](https://developer.riotgames.com/docs/portal))

OP.GG is not a clean substitute. Its December 2025 Help Center article says it generally does not prohibit crawling or web scraping if the source is cited and requests do not affect the service. However, OP.GG's still-published Terms of Use explicitly prohibit scraping, data mining, and automated scripts. Because those first-party statements conflict, programmatic ingestion should be treated as unresolved unless OP.GG gives written permission for this product. ([OP.GG Help Center](https://help.op.gg/hc/en-us/articles/31091405109401-Can-I-use-OP-GG-data), [OP.GG Terms of Use](https://op.gg/lol/policies/agreement))

For the next usable build, implement the historical-profile interface, UI states, caching, scoring experiments, and deterministic fake-data fixtures, but ship live historical lookup as gracefully unavailable unless an approved source is configured. A button that opens a player's normal OP.GG page in the user's browser is materially safer than reading that page into the overlay, but it is not an automated data source.

## Riot policy constraints

### Product registration and disclosure

- Riot says a product that serves players must be registered even if it does not use officially documented APIs. The product description and metadata must stay current. ([Riot LoL policy, Registration](https://developer.riotgames.com/docs/lol))
- The product must visibly include Riot's required “not endorsed by Riot Games” boilerplate. A repository README alone is not a clearly player-visible location; the installed product's About/help surface should contain it. ([Riot LoL policy, Developer API Policy](https://developer.riotgames.com/docs/lol))
- The League Client API is not officially supported: Riot gives no guarantees of complete documentation, uptime, or change notification. Riot also asks developers using it to register the product and identify the endpoints and their use. This remains relevant to the existing LCU-based lifecycle code regardless of whether historical data is added. ([Riot LoL documentation, League Client API](https://developer.riotgames.com/docs/lol))

### API keys and public distribution

Riot currently documents three relevant key classes:

| Key | Intended use | Published application limit | Public friend download? |
|---|---|---:|---|
| Development | Prototyping; deactivates every 24 hours | The portal does not present it as a public-product key | No |
| Personal | Developer or small private community | 20 requests/second and 100 requests/2 minutes, per region | No public consumption; Riot explicitly includes open alpha/beta in “public consumption” |
| Production | Public products | Starts at 500 requests/10 seconds and 30,000 requests/10 minutes, per region | Yes, after registration/approval |

Source: [Riot Developer Portal, API Keys](https://developer.riotgames.com/docs/portal).

Additional limits can apply per method and per service. A client must stop for the duration in `Retry-After` after HTTP 429; Riot warns that violating rate-limit policy can disable access. ([Riot Developer Portal, Rate Limiting](https://developer.riotgames.com/docs/portal))

Security requirements:

- Riot says an API key may not be included in code, especially when distributing a binary.
- One production key may only run one registered product.
- Requests to the official API must use HTTPS.

Source: [Riot LoL policy, Security](https://developer.riotgames.com/docs/lol).

**Architecture consequence:** a public WPF EXE cannot safely contain the product's production key. The practical official-API design is `desktop app -> product backend -> Riot API`, with the backend holding the key. Letting every friend paste a development key is not a usable product and does not convert those keys into permission to operate a public app.

### Game integrity and player evaluation

Riot's League policy sets several boundaries directly relevant to “who is strong/weak”:

- Do not use information absent from the game client to give a competitive edge.
- Do not create an unfair advantage.
- Do not dictate player decisions; products may highlight important decisions and offer multiple choices.
- Do not create an alternative to Riot's official skill ranking systems. Riot explicitly names MMR and ELO calculators as prohibited examples.
- Do not identify or analyze players deliberately hidden by the game.
- Do not provide game-session-specific information that was previously unknown to the player.
- Riot lists overlays that provide static data available before the game as an approved use case.

Source: [Riot LoL policy, Game Integrity and Game Policy](https://developer.riotgames.com/docs/lol).

These statements leave a meaningful product-design boundary:

- Displaying official rank and transparent summaries of recent, already-public matches is more defensible than inventing a hidden “true MMR,” “ELO,” or universal player strength rating.
- “Recent form” and “play style” should be separate dimensions with a sample size, time window, and confidence—not a replacement ladder.
- Avoid commands such as “camp this weak player,” target recommendations, or a current-match win probability derived from opponent history. Those are much closer to dictating decisions or creating an in-session competitive edge.
- Never obtain history for an anonymous champ-select slot or join it to a hidden identity. Fetch only after Riot normally reveals a Riot ID/PUUID.

The League policy does not explicitly approve this exact historical-profile UI. The phrase “static data available prior to the game” supports the concept, while the competitive-edge and alternative-ranking clauses constrain its presentation. The exact scoring/UI proposal should be included in the Riot product registration and, if necessary, asked through [Riot Developer Support](https://support-developer.riotgames.com/).

Custom-match history has an additional explicit rule: it may not be displayed publicly unless that player opts in; otherwise it may only be made available to that player through RSO. The first version should simply exclude custom queues. ([Riot LoL policy, unapproved use cases](https://developer.riotgames.com/docs/lol))

## Official Riot data that is available

The current official API reference includes:

- `ACCOUNT-V1 /riot/account/v1/accounts/by-riot-id/{gameName}/{tagLine}` to resolve a Riot ID to PUUID. Riot now requires player-facing lookup/display to use Riot IDs and recommends PUUID for downstream APIs. ([Riot Riot-ID migration documentation](https://developer.riotgames.com/docs/lol), [Account API reference](https://developer.riotgames.com/apis#account-v1/GET_getByRiotId))
- `MATCH-V5 /lol/match/v5/matches/by-puuid/{puuid}/ids` to list match IDs. The reference currently accepts time, queue and type filters, defaults to 20 IDs, and allows 0–100 IDs per call. ([Match-v5 reference](https://developer.riotgames.com/apis#match-v5/GET_getMatchIdsByPUUID))
- `MATCH-V5 /lol/match/v5/matches/{matchId}` for match participant statistics, and the timeline endpoint for event/frame data. ([Match reference](https://developer.riotgames.com/apis#match-v5/GET_getMatch), [Timeline reference](https://developer.riotgames.com/apis#match-v5/GET_getTimeline))
- `LEAGUE-V4 /lol/league/v4/entries/by-puuid/{encryptedPUUID}` for official ranked entries. ([League-v4 reference](https://developer.riotgames.com/apis#league-v4/GET_getLeagueEntriesByPUUID))

For Taiwan, Riot documents platform route `TW2` and regional route `SEA`; the API reference determines which route each endpoint uses. ([Riot routing values](https://developer.riotgames.com/docs/lol))

Potential features supported by these sources include official queue rank, recent match results, champion/role distribution, and transparent style dimensions derived from recent match participants. Exact field availability can change, so the implementation must tolerate missing fields and version the feature extractor.

### Cost shape

For ten players and a 20-match window, a naive refresh may require approximately:

- up to 10 identity resolutions if only Riot IDs are available;
- 10 ranked-entry calls;
- 10 match-ID-list calls;
- up to 200 match-detail calls.

That estimate is an inference from the documented endpoint shapes, not a published Riot quota. It already exceeds a personal key's 100 requests/2-minute application limit if performed cold. A production implementation needs shared match deduplication, cached identity/rank/profile results, bounded concurrency, 429 backoff, and a stale result with “last updated” rather than blocking the overlay.

## Source strategy comparison

| Strategy | Policy position | Stability | Privacy/operations | Recommendation |
|---|---|---|---|---|
| Official Riot API | Documented and suitable for registered products, subject to approval and game policy | Highest of the three, though fields/endpoints can deprecate | Requires production key, secure backend, rate limiting, and disclosure that visible player IDs are sent to the backend | Long-term source |
| User opens an OP.GG page | Ordinary user navigation; no data is automatically copied into this product | Page remains OP.GG's responsibility | No overlay-side player-history store; browser/OP.GG have their own privacy behavior | Safe optional convenience link, not a data provider |
| Overlay parses a public OP.GG page | First-party Help Center appears permissive, but first-party Terms explicitly prohibit scraping/data mining/automated scripts | Brittle HTML/localization/anti-bot dependency | Sends player queries to OP.GG; may be blocked; source attribution required by Help Center | Do not ship without written clarification/permission |
| OP.GG private/internal endpoint | OP.GG says its game data is not provided to third parties; no supported public developer contract was found | Very low; endpoint/auth/schema can change without notice | Access restriction and breakage risk; may expose cookies or identifiers | Do not use |
| Unsupported Riot/LCU private history endpoint | LCU is explicitly unsupported and may change without notice | Low | Must still register and disclose endpoints; may only expose self data | Do not make it the historical-profile dependency |

### OP.GG first-party conflict

Three first-party documents matter:

1. OP.GG's [December 31, 2025 Help Center article](https://help.op.gg/hc/en-us/articles/31091405109401-Can-I-use-OP-GG-data) says OP.GG does not provide its collected game data to third parties. It also says, in general, OP.GG does not prohibit crawling/web scraping, but commercial use without citation or excessive requests affecting operations may be restricted.
2. OP.GG's [Terms of Use](https://op.gg/lol/policies/agreement), effective January 10, 2024 and still linked on the live site, prohibit scraping/data mining and separately prohibit automated scripts that collect information or interact with the service.
3. OP.GG's current [`robots.txt`](https://op.gg/robots.txt) permits `/` for the generic user agent, while setting separate rules for named crawlers.

The newer Help Center wording may be intended as a clarification, but it does not explicitly amend or supersede the Terms, and the two statements cannot be reconciled confidently from the published text alone. `robots.txt` is a crawler instruction, not an explicit product/data license; that sentence is an engineering/legal-risk inference, not an OP.GG statement.

Therefore:

- Do not call undocumented OP.GG JSON endpoints, replay browser requests, bypass access controls, or ship an HTML scraper.
- If OP.GG ingestion remains desirable, send OP.GG a written request describing the exact noncommercial open-source overlay, request rate, cached fields, attribution, and in-game display. Retain the written answer.
- Until then, an “Open OP.GG profile” button may construct the normal public profile URL and hand it to the user's default browser. Do not read browser DOM/cookies back into the app.

## Privacy and data-retention design

Historical data changes the existing privacy story. If a backend is introduced, the guide can no longer say the program has no remote server or never sends player data.

Minimum design:

- Fetch only normally revealed Riot IDs/PUUIDs; never resolve hidden slots.
- Exclude custom queues.
- Send the minimum identifier and region needed for lookup.
- Cache aggregates rather than retaining full raw match payloads indefinitely.
- Give cached records an expiry and expose `last updated`/`stale` in the UI.
- Do not log API keys, LCU credentials, PUUIDs, raw match responses, or full URLs containing identifiers by default.
- Document the backend hostname, purpose, retained fields, retention period, and deletion/contact path before enabling it.
- Keep historical profile output separate from current-match performance output so the UI never misrepresents one as the other.

These are risk-minimizing product recommendations. Riot's cited policies require legal compliance and protect API keys, but they do not prescribe these exact retention periods or UI labels.

## Safe fake-data testing

Fake-data testing is the safest way to build the feature before a source is approved:

- Use obviously synthetic Riot IDs and structurally valid but invented PUUIDs/match IDs.
- Generate deterministic match summaries covering roles, champions, wins/losses, remakes, missing fields, queue filters, low sample size, conflicting style signals, 429, 404, 5xx, offline, stale cache, and partial ten-player results.
- Do not copy real OP.GG page content, response cookies, private endpoints, API keys, or real friend histories into committed fixtures.
- Label replay/profile data as synthetic in developer builds. The friend-facing production UI should never imply synthetic history is real.
- Test the scoring model against invariants: no alternative MMR/ELO label, no target instruction, no hidden identity, clear sample size/confidence, and deterministic graceful degradation.

No Riot/OP.GG request is made in this mode, so it does not consume API quotas or scrape either service.

## Recommended v1 architecture

Create a source-neutral module rather than coupling the overlay to a website:

```text
Normally revealed player identities
            |
            v
HistoricalProfileCoordinator
  - cache / deduplicate / timeout / cancellation
  - returns Available, Partial, Stale, Unavailable, or PolicyDisabled
            |
            +--> SyntheticHistoricalDataProvider (fixtures and replay)
            +--> RiotHistoricalDataProvider (backend only, when approved)
            +--> NoHistoricalDataProvider (shipping fallback)

OP.GG profile link = separate browser action, not a provider
```

Suggested public interfaces:

```csharp
public interface IHistoricalProfileProvider
{
    Task<HistoricalProfileResult> GetProfilesAsync(
        IReadOnlyList<RevealedPlayerIdentity> players,
        CancellationToken cancellationToken);
}

public enum HistoricalProfileAvailability
{
    Available,
    Partial,
    Stale,
    Unavailable,
    PolicyDisabled
}
```

The display model should contain only explainable aggregates such as:

- official rank (if returned);
- recent match window and queue;
- recent form, with record and confidence;
- common roles/champions and champion-pool breadth;
- descriptive play-style dimensions with plain-language definitions;
- source and last-updated timestamp.

Avoid fields named `MMR`, `ELO`, `true rank`, `player power`, or a global 0–100 skill rating. If the team still wants one historical “strength” score, do not ship it until Riot has reviewed the exact formula and UI; a transparent recent-form band is lower-risk than an alternative ladder.

Operational behavior:

- Historical lookup must never delay Dot/Compact/Expanded live rendering.
- Show `歷史資料暫時無法取得` or stale cached values rather than a spinner that blocks interaction.
- Fetch once when identities are revealed, not every one-second live tick.
- Rate-limit and coalesce all ten players; use short request deadlines and cancellation at end-of-game.
- Treat history as optional enrichment. Current-game scoring must remain fully usable with no history.

## What can realistically be complete tomorrow

Can be complete and honestly called usable:

- smooth overlay interaction and drag behavior;
- the source-neutral historical-profile module;
- fake-data/replay scenarios and UI for available/partial/stale/unavailable states;
- an optional browser button to open a public OP.GG profile;
- a normal package that works without historical data;
- clear documentation that history is optional and source-dependent.

Cannot be responsibly guaranteed by tomorrow unless it already exists:

- Riot production-key approval;
- a reviewed, deployed, privacy-documented backend;
- written OP.GG permission resolving the published conflict;
- a policy-safe live historical strength score for current opponents.

The release should not silently substitute scraping just to meet a date. “Fully usable” should mean the core overlay remains useful and responsive when history is unavailable, while the historical module can be activated later without redesigning the UI or domain model.

## Sources checked

- [Riot League of Legends developer policy and documentation](https://developer.riotgames.com/docs/lol)
- [Riot Developer Portal: keys, public-product rules, rate limits](https://developer.riotgames.com/docs/portal)
- [Riot API Terms](https://developer.riotgames.com/terms)
- [Riot API reference](https://developer.riotgames.com/apis)
- [OP.GG: Can I use OP.GG data?](https://help.op.gg/hc/en-us/articles/31091405109401-Can-I-use-OP-GG-data)
- [OP.GG Terms of Use](https://op.gg/lol/policies/agreement)
- [OP.GG robots.txt](https://op.gg/robots.txt)
