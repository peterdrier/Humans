# Debt Sweep Report — 2026-06-14

- **Started:** 2026-06-14T19:09:01Z
- **Budget:** 2h (default); finished well within budget (~35 min)
- **Branch:** `debt-sweep/2026-06-14T190908Z` (off `origin/main` @ `c9749fe90`)
- **Worked theme:** `inbox` (fell through to it — see Rotation below)

## Rotation — this ledger is in a "hard cores only" state

The two oldest (`never`) panel themes yield no sweep-mechanical fixes, so the run
fell through to the `inbox` theme:

1. **`cs0618-pragma-nav-reads`** (pick #1, `never`) — audited all 27 pragma'd
   files → **0 cross-section nav-read fixes**. The detect counts FILES-with-pragma
   and conflates five unrelated things, none sweep-mechanical:
   - ~15 `Data/Configurations/*.cs` HasOne nav **mappings** = the SCHEMA side
     (`grandfathered-hum0024-nav-strip`, schema-blocked);
   - 3 **false positives** where the arch scan's `.User` textual heuristic matches
     a non-domain member — `HttpCurrentUserContext` + `CurrentUserEnricher`
     (`HttpContext.User`), `AccountController` (record field `result.User`);
   - 2 obsolete `ContactFieldType.Email` **enum** reads (legit legacy display, a
     different obsolete concern) — `ProfileApiController`, `ProfileViewModel`;
   - the **approved §6b in-memory stitch** (already migrated) — `FeedbackService`,
     `TeamService` (`member.User`) + consumers `AboutController`, `ProfileController`,
     `TeamController`, `TeamAdminController`, `TeamViewModels`;
   - 3 **in-section** Team-nav reads in `TeamService` — off-theme; the only "fix"
     relocates the pragma into a repo projection (still CS0618), non-mechanical.

   Like `obsolete-user-displayname`, the detect floors (~27) and can't reach 0
   without forbidden pragmas/schema work. **Parked**; `last_swept` bumped to
   2026-06-14; ledger note rewritten with the 5-category breakdown + re-scope
   recommendation.

2. **`baseline-entity-read-returns`** (pick #2, `never`) — passed over: all 21
   entries are public-interface read methods whose fix CHANGES the return type
   entity→DTO, rippling to every caller (e.g. `IUserService.GetByIdsAsync:User`,
   `ITeamService.GetTeamByIdAsync:Team` are pervasive). Per the work-loop's
   skip-and-ask rule (public-surface changes), each is skip-and-ask. `last_swept`
   left `never`; note added. **Needs Peter's call** (see Questions).

## Fixed

- **`inbox`: View components must not inject `IMemoryCache`** (this PR) —
  `NavBadgesViewComponent` no longer injects `IMemoryCache`. Its three nav-badge
  counts now resolve through their owning services:
  - **feedback count** → cached in `FeedbackService.GetActionableCountAsync`
    (`CacheKeys.FeedbackBadgeCount`); wraps a real repo query, evicted by the
    existing `INavBadgeCacheInvalidator`.
  - **voting count** → cached in `ApplicationDecisionService.GetUnvotedApplicationCountAsync`
    (`CacheKeys.VotingBadge(userId)`); a **consolidation** — the same cache moved
    out of both the VC and `NotificationMeterProvider` (whose redundant inline copy
    was removed) into the one owning service, evicted by `IVotingBadgeCacheInvalidator`.
  - **review count** → reads `AdminDashboardService.GetPendingReviewCountAsync`
    directly with **no new cache**: its source (`GetAllUserInfosAsync`) is already
    cache-served by `CachingUserService` (write-through, fresh-on-write).
  - The bundled static `NavBadgeCounts` (`(review, feedback)` tuple) was retired;
    `InvalidateNavBadgeCounts()` now clears `FeedbackBadgeCount` only (review needs
    no eviction — it's fresh-on-write).
  - `FeedbackService` + `ApplicationDecisionService` added to the
    `ApplicationServicesTakeNoMemoryCache` architecture allowlist with rationale
    (sanctioned "add when intentional and reviewed" path; precedent: `IssuesService`,
    `NotificationMeterProvider` already allowlisted for the identical badge-count
    concern). **`AdminDashboardService` deliberately NOT allowlisted.**
  - Build: 0 errors, 0 new warnings. Tests: 4627 passed, 0 failed.
  - Files: `NavBadgesViewComponent.cs`, `FeedbackService.cs`,
    `ApplicationDecisionService.cs`, `AdminDashboardService.cs`,
    `NotificationMeterProvider.cs`, `CacheKeys.cs`, `MemoryCacheExtensions.cs`,
    `ApplicationServicesTakeNoMemoryCacheRule.cs` + 4 touched test files.

## Panel review

Elevated this `light` inbox item to a **panel** review (opus, score-blind,
default-reject) because it expanded an architecture allowlist.

- **First verdict: REJECT.** The reviewer caught a §4b anti-pattern in the
  **review-count** half: `GetPendingReviewCountAsync` reads `GetAllUserInfosAsync`,
  which is **already** cache-served by the write-through `CachingUserService`.
  Layering a 2-min TTL `ReviewBadgeCount` cache on top added a staleness window,
  **regressed** `AdminController`/`AdminNavTree` (which had fresh-on-write reads),
  and had an eviction gap (`OnboardingService` never calls `navBadge.Invalidate()`).
  The voting (consolidation) and feedback (real DB query) halves were sound.
- **Rework (one allowed):** applied the reviewer's prescription verbatim — dropped
  the review-count cache entirely, removed `ReviewBadgeCount`, kept
  `AdminDashboardService` off the allowlist; kept feedback + voting. Re-built +
  re-tested green. The reworked design is strictly better than both the original
  code and the first attempt.

## Skipped

- All `cs0618-pragma-nav-reads` items — non-actionable (see Rotation).
- All `baseline-entity-read-returns` items — skip-and-ask (public-surface return
  changes).
- No forbidden-move reverts. No EF drift (no entity/nav/config touched).

## Inbox additions

- Removed the completed "ViewComponents injecting IMemoryCache" item; added a new
  item for the **VotingBadge eviction-completeness gap** surfaced by Codex on PR
  #1010 (pre-existing, TTL-bounded — submit/withdraw/non-voter-resolve don't clear
  per-user voting badges). Net inbox count unchanged at 10.

## PR #1010 review triage (/pr-fix)

- **claude bot** — "no issues found" (no action).
- **Codex P2** — *"Invalidate all affected voting badge counts."* DECLINED in this
  PR + recorded as inbox debt. Valid observation but pre-existing: the
  VotingBadge(userId) eviction wiring is UNCHANGED by this PR (same key/TTL/invalidator
  existed in the VC + NotificationMeterProvider before). This PR only makes AdminNavTree
  share the same cache — the intended owning-service caching outcome, matching the nav
  badge's long-standing 2-min staleness, for a real DB query the panel endorsed caching.
  Broadcast-invalidating all board members on submit/withdraw/resolve is a correctness
  improvement that touches 4 mutation paths + needs Board-role enumeration → its own
  targeted pass (now ledgered).

## Ledger changes

- `inbox`: `last_swept` → 2026-06-14, `remaining` 10 → 9 (ViewComponent item removed).
- `cs0618-pragma-nav-reads`: `last_swept` `never` → 2026-06-14; note rewritten
  (5-category audit, parked, re-scope recommendation). `remaining` 27 (floored).
- `baseline-entity-read-returns`: note added (all skip-and-ask); `last_swept` stays
  `never`.
- `recent_sections`: `[Camps, Campaigns, Shifts]` → `[Campaigns, Feedback, Governance]`.
- No new themes, retirements, or evictions.

## Questions for Peter (resolved inline at end of run)

1. **Allowlist expansion** — OK to allowlist `FeedbackService` +
   `ApplicationDecisionService` for inline badge-count caching (matching
   `IssuesService`/`NotificationMeterProvider`), or do you want Infrastructure
   `Caching*Service` decorators (§15 Option B) instead?
2. **`baseline-entity-read-returns`** — do the 21 entity→DTO return-type
   migrations belong in the sweep (each is a public-surface change touching many
   callers), or as dedicated per-method PRs? Until decided it stays skip-and-ask.
3. **`cs0618-pragma-nav-reads`** — agree to park it (floored at ~27, 0 actionable)
   and re-scope its detect to count only un-stitched cross-section nav reads?
