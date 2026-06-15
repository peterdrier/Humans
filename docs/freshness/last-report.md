# Freshness Sweep Report — 2026-06-15

**Anchor:** `909323e33` → `c10d07400` (upstream/main HEAD)
**Worktree base:** `origin/main` @ `c10d07400`
**Mode:** diff (batch)
**Changed files in range:** 66
**Dirty entries processed:** 7 mechanical + 23 editorial = 30
**Outcome:** 8 docs updated + 3 agent docs given `freshness:triggers` (Phase 7.5) · 0 husks pruned (nothing aged out) · 22 entries verified clean · 0 errors

> **Anchors in sync.** At sweep start `origin/main` and `upstream/main` were both at `c10d07400` — a prod promotion landed just before this run, so the two remotes are identical. `merge-base --is-ancestor upstream/main origin/main` is true; nothing crossed.

The `909323e33..c10d07400` range carries three themes: (1) the **community knowledge-base agent** feature (#1008, #1013, #1014 — new `fetch_community_faq` tool, a separate `nobodies-collective/knowledge-base` GitHub repo cached in RAM, a "Reload KB" admin button, and a system-prompt token-count badge on the prompt-preview page); (2) the **NavBadges caching → owning services** debt sweep (#1010 — `NavBadgesViewComponent` stopped injecting `IMemoryCache`; the feedback badge count now caches inside `FeedbackService` as `FeedbackBadgeCount`, the voting count inside `ApplicationDecisionService` as `NavBadge:Voting:{userId}`; both services were added to the `ApplicationServicesTakeNoMemoryCacheRule` allowlist); and (3) a **cosmetic logo/favicon** redesign (#1006, #1007). Themes (2) drove real doc drift (stale "no `IMemoryCache`" invariants); themes (1) and (3) were contained.

## Updated automatically

- **dev-stats** — appended the 2026-06-15 codebase-growth row (`generate-stats.sh`).
- **reforge-history** — appended the 2026-06-15 semantic-metrics row (`generate-reforge-history.sh`).
- **service-data-access-map** — retired the `NavBadgeCounts` key throughout (now `FeedbackBadgeCount`, owned by `FeedbackService.GetActionableCountAsync`); moved `NavBadge:Voting:{userId}` ownership from `NavBadgesViewComponent`/`NotificationMeterProvider` to `ApplicationDecisionService.GetUnvotedApplicationCountAsync`; removed `NavBadgesViewComponent` from every cache-owner/read-write table and appendix; corrected the "view components still populate two caches" note to one (`NotificationBellViewComponent`). Verified against `CacheKeys.cs`, `FeedbackService.cs`, `ApplicationDecisionService.cs`.
- **controller-architecture-audit** — added the new `AdminAgentController.ReloadKnowledgeBase` (`POST /Agent/Admin/ReloadKnowledgeBase`) action row; bumped the "Last updated" date.

## Editorial drift fixed in place

The #1010 refactor moved nav-badge caching out of `NavBadgesViewComponent` into the owning services and added a view-component-may-not-inject-`IMemoryCache` analyzer rule. Four docs still asserted the pre-refactor invariants and were corrected against current source (`ApplicationServicesTakeNoMemoryCacheRule.cs`, `FeedbackArchitectureTests.cs`, `GovernanceArchitectureTests.cs`, the two services):

- **conventions.md** §view-components — "view components that query aggregate data *may* use `IMemoryCache`" → now states they **must not** (a documented convention from `memory/code/viewcomponent-no-cache.md`; the analyzer in `roslyn-analysis.md` is proposed but not yet built — `Current coverage: none`), and the cache lives inline in the owning service with the view component as a thin pass-through.
- **sections/Feedback.md** — the "Caching" bullet now names the inline `FeedbackBadgeCount` (2-min) cache; the architecture-test bullet no longer claims a pinned "no `IMemoryCache` constructor param" (the check is delegated to the allowlist rule, which now permits `FeedbackService`); the touch-and-clean "do not inject `IMemoryCache`" rule was inverted to describe the now-correct inline badge cache.
- **sections/Governance.md** — same correction for `ApplicationDecisionService` (`NavBadge:Voting:{userId}`, allowlisted); `MembershipCalculator`/`MembershipQuery` still hold no cache.
- **design-rules.md** §15 Governance note — "dropped its caching layer entirely / DB reads per request are fine" reframed to "dropped its §15 projection layer entirely," with the one #1010 exception (the per-board-member badge count cached inline 2-min) called out as a request-acceleration cache, not a §15 projection.

## Verified clean (dirty but no drift)

- **Mechanical (regen no-op): 3** — `docs-readme-index` (only `Auth.md`'s freshness-comment header changed, not its intro paragraph → README description unchanged); `authorization-inventory` (the new `ReloadKnowledgeBase` action inherits the class-level `PolicyNames.AdminOnly`, no new `[Authorize]`); `dependency-graph` (the only Application-service ctor change was `+IMemoryCache`, which is not a graphed node; fan-in/out unchanged).
- **Editorial (no drift): 19** — the caching-refactor-triggered docs (`features/feedback`, `features/issues`, `features/notifications`, `features/governance/*`, `features/onboarding/onboarding-pipeline`, `features/global/gdpr-export`, `guide/{Feedback,Governance,Admin}`, `sections/{Notifications,Onboarding,Guide}`) describe behaviour, not cache internals — no editorial doc referenced the renamed key (verified by grep), and behaviour is unchanged. `features/global/administration.md` has no agent-page prose to drift; `guide/Admin.md` only lists "Agent" as a sidebar group. `sections/admin-shell.md` + `code-review-rules.md` + `roslyn-analysis.md` triggered on cosmetic icon-link / generic-rule changes with no factual contradiction (`roslyn-analysis.md` already documents the view-component no-cache rule). `sections/Guide.md` + `features/guide/in-app-guide.md` triggered on `GitHubGuideContentSource.ListMarkdownStemsAsync` — a purely additive method serving the community KB, no guide-browser behaviour change.

## Pruned

None this sweep. Strict filename-date aging (today = 2026-06-15):
- `docs/plans/*` — earliest is `2026-05-16-cache-migration.md` (exactly 30 days, not yet *older* than 30 — eligible 2026-06-16). None eligible.
- `docs/superpowers/plans/*` — earliest is `2026-05-18-store-summary-aggregates.md` (28 days). None eligible.
- `docs/superpowers/specs/*` (60-day rule) — earliest is `2026-04-19-volunteer-coordinator-dashboard-design.md` (57 days, eligible 2026-06-18). None eligible.
- `docs/architecture/tech-debt-2026-04-23.md` — **not** all-`[DONE]` (its own summary notes layering/purity items "still real"). Retained.
- No orphan refs to the husks the prior sweep (#1009) deleted.

**Next-sweep candidates:** `2026-05-16-cache-migration.md` (2026-06-16), then the `2026-05-18`/`2026-05-20` superpowers/plans, and `2026-04-19`+ specs (from 2026-06-18).

## Flagged for human review

None outstanding. The one flag this sweep — the unmarked agent docs lagging the community-KB feature — was resolved in the Phase 7.5 follow-up commit (see Questions).

## Unmarked editorial blind spots (carried forward)

`features/{26-events, 27-guide-browser, 43-google-group-membership-sync, test-system-reliability, user-search-overhaul}.md`, `guide/{EmailAccount, SigningIn, TicketTransfers, TwoStepVerification, YourData}.md`, `sections/{Mailer, _Index}.md`. None triggered this sweep (no `freshness:triggers`). The three agent docs were removed from this list this sweep — they now carry `freshness:triggers`.

## Proposed for review

None — all candidates resolved this sweep.

## Questions

**Resolved this sweep (Peter, inline):** the three agent docs (`sections/Agent.md`, `features/agent/agent-section.md`, `guide/AiHelper.md`) were unmarked and lagging the community-KB feature. Per Peter's "yes add", `freshness:triggers` + `freshness:flag-on-change` blocks were added to all three (scoped to the agent services / preload+community-KB readers / tool catalog / agent controllers; the guide doc scoped to user-visible surface only). The community-KB **content rewrite** of these docs is deferred to a dedicated docs task — out of scope for a drift-fix sweep. The next sweep will now flag these docs on agent-code changes.

## Skipped (errors)

None.
