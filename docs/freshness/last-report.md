# Freshness Sweep — 2026-08-12

| | |
|---|---|
| Mode | diff |
| Previous anchor | `04d2490f0` |
| New anchor | `044b9e77a` |
| Commits in window | 16 (13 non-merge) |
| Changed files in window | 1,244 |
| Worktree base | `origin/main` @ `fa8b36737` (frozen) |
| Mechanical entries dirty | 9 of 11 |
| Editorial docs dirty | 118 of 137 (+4 recovered, see *Systematic findings*) |

The window is dominated by the **G5 section split** — 737 of the 1,244 changed files are under
`src/Sections/`. Agent and Surveys moved into their own projects, bringing the total to 19 section
projects; a brand-new **Humans.Holded** section landed (Holded API v2, full ledger mirror,
reconciliation, `/Holded` admin screen); the **per-section DbContext split** continued (Shifts →
`ShiftsDbContext`, Legal + AuditLog peeled with immutability triggers, Gate peeled, `SystemDbContext`
created for `DataProtectionKeys`); and a **G5 keystone analyzer (HUM0034)** now enforces
internal-by-default inside section assemblies. Nearly all drift this sweep is downstream of one of
those four.

## Systematic findings

**27+ dead `freshness:triggers` globs across 16 docs — and four docs that had stopped firing
entirely.** `docs/guide/Calendar.md`, `Campaigns.md`, `CityPlanning.md`, and `Feedback.md` had
*every* trigger pointing at a path the G5 moves had killed, so they never appeared in a dirty list
and had not been drift-checked for several sweeps. All four were given repaired triggers plus a full
content pass, which turned up 15 substantive content corrections — including a documented feature
that cannot happen (see *Campaign unsubscribe* below). Twelve more docs had partial dead globs.

A glob-aware verifier now reports **0 dead paths across all 138 trigger-bearing docs.**

This is the **third consecutive sweep** to find a large dead-trigger batch: 64 across 44 docs two
sweeps ago, 37 across 8 docs last sweep, 27+ across 16 docs now. The failure is silent by
construction — a doc with a dead glob looks *clean*, not *unchecked* — and it recurs every time a
refactor moves files. This wants a CI check, not a once-per-sweep repair.

**A brand-new section was invisible to the sweep.** `src/Sections/Humans.Holded/Docs/Holded.md`
shipped with no `freshness:triggers` marker at all, so the newest section in the codebase would
never have been drift-checked. Marker added. `docs/sections/G5-SECTION-TEMPLATE.md` was also
unmarked; it is a template, so it went into the catalog's `ignore` list beside `SECTION-TEMPLATE.md`.

**A trigger gap in `conventions.md` itself.** Its trigger block covered
`src/Humans.Web/ViewComponents/**` and `src/Humans.Web/Views/**` but not `src/Humans.UI/**`, where
the shared view layer, the section-agnostic view components, and `DateTimeDisplayExtensions` now
live. Added.

**Two lanes contradicted each other, and cross-checking caught it.** The regenerated
`service-data-access-map.md` claimed `ISurveyServiceRead` "is registered but empty pending a
consumer"; the G5 lane reported it deleted. The code settles it — `Humans.Surveys/Services/ISurveyService.cs:14`
carries the comment *"There is no `ISurveyServiceRead`: it shipped empty in v1 and no other section
ever…"*, and `SurveysArchitectureTests.cs:19` confirms the paired test went with it. Corrected.
The same lane left `TicketingBudgetService` filed under `## Tickets` with no note that it now lives
in `Humans.Budget`; an ownership note was added.

## Updated automatically

- `dev-stats` — script-driven; 1 row appended (142 data rows).
- `reforge-history` — script-driven; CSV at 131 rows / 131 distinct days.
- `authorization-inventory` + `controller-architecture-audit` — G5 relocations verified line-by-line
  as namespace-only (no role/policy/attribute changes). Added the new Holded section
  (`HoldedController`, class-level `FinanceAdminOrAdmin`, 4 actions). Removed
  `FinanceController.ResyncCreditorLedger` (deleted, superseded by `HoldedController.FullSync`).
  `PolicyNames.AgentRateLimit` was deleted — `AgentController.Ask` now instantiates
  `AgentRateLimitRequirement` directly; propagated across five tables. Controllers audited 90 → 91.
- `dependency-graph` + `service-data-access-map` — regenerated across `HumansDbContext` plus all
  per-section contexts. Cycle #6 (**Camp ↔ CityPlanning**) documented and refined; the
  GoogleWorkspaceSync ↔ TeamResource cycle's eager half is now gone.
- `docs-readme-index` — all 69 feature links, all section-invariant links and all 25 guide links
  verified to resolve on disk; added the missing row for the Holded section doc.
- `about-page-packages` — **verified clean.** All 47 production packages and versions already matched
  `Directory.Packages.props`.
- `code-analysis-suppressions` — **verified clean.** The auto-block already matched both
  `Directory.Build.props` files exactly.
- `data-model-index`, `guid-reservations` — not dirty this window.

## Editorial drift fixed

118 dirty docs across 10 lanes; ~66 files edited, the rest verified accurate and deliberately left
untouched. Highlights, all verified against current source:

**Recovered guide docs (had stopped firing).** Calendar: all-day events were entirely undocumented
(inclusive end date, half-open normalization); team dropdown is active-and-not-hidden only.
CityPlanning: container placement is a **separate phase** (`IsContainerPlacementOpen`), which the doc
folded into barrio placement. Feedback: the API surface was understated by five endpoints and
assignment was undocumented; the detail view is a panel, not a page.

**Wrong entity fields.** `CalendarEventException`'s field names were *all* wrong —
`Title`/`StartUtc`/`EndUtc` are actually `OverrideTitle`/`OverrideStartUtc`/`OverrideEndUtc`, plus
four more `Override*` fields and `CreatedByUserId` were missing.

**Deleted symbols still documented as live.** `IFeedbackService` (→ `IFeedbackServiceRead`, 6 sites),
`ISurveyServiceRead`, `ITicketingBudgetService`'s old home, `IssuesWidgetViewComponent`,
`BoardController`, `AdminDuplicateAccountsController` + `AdminMergeController` (deleted in #899),
`DriveActivityMonitorRepository`, `GeneralAvailabilityService`, and `GoogleSyncService` (no such
concrete class — it is `GoogleWorkspaceSyncService`).

**Architecture-doc corrections (~30).** The G5 list said "six so far"; it is 19. Identity tables are
renamed to snake_case in `OnModelCreating`, not `AspNetUsers`. Eight wrong table names
(`audit_log_entries` → `audit_log` at four sites, `holded_sync_states` → `holded_doc_sync_state`,
…). `team_pages` removed — no such table exists; `TeamPageService` is a composer. Five authorization
handlers were missing from the §11 inventory. Repository count 35 → 36. `code-review-rules.md`'s
"Missing `.Include()`" rule told reviewers to demand a cross-section `.Include()` that **HUM0024 now
fails at build time** and that #996/#992 made impossible — carve-out added.

**Governance/onboarding.** `TermExpiresAt` runs to the next odd year at least two years out, and the
first term runs **long** — the doc said short, contradicting both `TermExpiryCalculator` and the
doc's own worked examples. `MembershipCalculator` computes `IncompleteSignup` before `Suspended`.
Cross-section FK notations corrected to bare `Guid` columns throughout (no FK constraint, no nav).

**`docs/sections/_Index.md`** — all 23 relative links verified to resolve, 0 broken. Holded added to
the G5 map; the duplicate second `**Holded**` row renamed `**Holded Connector**` and cross-linked, so
the ledger-mirror section and the Base HTTP connector are distinguishable.

## Pruned

**3,317 lines removed (6.2% of 53,856 doc lines)** — above the 5% target, under the 7% cap.

| Husk | Lines | Reason |
|---|---|---|
| `docs/superpowers/plans/2026-06-10-table-component.md` | 1,482 | all chaff |
| `docs/superpowers/plans/2026-06-04-datetime-format-analyzer.md` | 705 | all chaff |
| `docs/superpowers/plans/2026-06-04-per-day-shift-signup.md` | 639 | all chaff |
| `docs/superpowers/plans/2026-06-08-scanner-ticket-lookup.md` | 491 | all chaff |

**Wheat migrated: none — and that is the finding.** Every candidate was verified against code and
failed. In all four cases the durable rationale already lives in the husk's **retained design spec**,
and the plan-only residue proved *stale*:

- The datetime husk's format-literal→method mapping table names methods that do not exist — shipped
  names on `DateFormattingExtensions` are `ToDate`/`ToDateTime`/`ToWeekdayDayMonth`/`ToInvariantDate`/
  `ToIso8601`/`ToSepaDateTime`/`ToFileTimestamp`.
- The table-component husk places the component at `src/Humans.Web/Models/Tables/`; it shipped in #929
  and moved to `src/Humans.UI/Models/Tables/` at G5. `EnumBadgeMap.cs:12-27` now carries richer
  rationale than the plan ever did.
- The per-day-shift-signup husk's headline claim — *"adds no new service/interface methods"* — is
  false: `ShiftsController.ToggleDay` delegates to a new `IShiftSignupService.ToggleDayAsync`.
- The scanner husk's cache-invalidation note reaches the **opposite** conclusion from shipped code;
  `Tickets.md` already documents the real behavior.

Inbound refs: **zero**, independently re-verified across `*.md`/`*.yml`/`*.cs`/`*.json`. The single
grep hit — `src/Humans.UI/Models/Tables/ITableModel.cs:8` — points at the *retained spec*, not a
deleted plan. Nothing to retarget.

## Flagged for human review

None. Every concrete, code-checkable contradiction was fixed inline. The items below are genuine
judgment calls or out-of-scope app gaps, and were delivered to Peter inline (see *Questions*).

## Proposed for review

None — all prune candidates resolved this sweep.

## Questions — all answered inline, all resolved this sweep

Peter answered every item. Resolutions applied in this PR:

| # | Answer | What was done |
|---|---|---|
| 1 | Campaign codes stay **not** opt-out-able | Code fix filed as nobodies-collective/Humans#1032 — suppress the footer link + `List-Unsubscribe` headers for always-on categories. Docs already corrected to match the code. |
| 2 | City Planning issues go to **City Planning**, not Admin | Filed nobodies-collective/Humans#1033. |
| 3 | Add `/Holded` to the **Money** group | Filed nobodies-collective/Humans#1034. |
| 4 | Add the Camp Coordinator role mapping | Filed nobodies-collective/Humans#1035. |
| 5 | Not orphaned — they have **dev-only** links | Doc was wrong. The buttons are `AdminNavTree`'s **Dev** group, gated `EnvironmentGate: env => !env.IsProduction()`. The worker only searched `.cshtml` form markup and missed the nav tree. `seed-data.md` corrected. |
| 6 | Google Integration owns it | §8 change stands — and it matches the pre-existing rule atom [`team-resources-google-integration-section`](../../memory/architecture/team-resources-google-integration-section.md), which says section labels follow *code locality*, not table aggregate ownership. |
| 7 | Leave for now | No action. `CampaignService.ImportCodesAsync` keeps its UI-only status gate. |
| 8 | **Frozen until 2027** | `roslyn-analysis.md` retied the freeze to a **date** instead of #866 — tying it to an issue is why agents kept re-raising it every time #866 advanced. Captured as a rule atom: [`hum0031-frozen-until-2027`](../../memory/architecture/hum0031-frozen-until-2027.md). Do not raise this again in any form. |
| 9 | Add `memory/` to the catalog | Added to `editorial_trees`, with `INDEX.md`/`META.md` ignored. The two stale `Humans.Web.Extensions.DateTimeDisplayExtensions` atoms corrected to `Humans.UI.Extensions`. Its 152 rule atoms carried **zero** freshness markers, so adding the tree bare would have flooded the next sweep with ~152 "unmarked" flags — **103 atoms marked in the same pass**, each glob resolved against a real file and verified. The other 50 are pure policy (git etiquette, working habits) that cannot rot against source and were deliberately left bare. |
| 10 | Remove if gone, rewrite if moved | **54 of 57** wheat markers pointed at sources deleted by earlier prunes; none of those files exist anywhere in the repo, so none could be rewritten — all 54 removed, prose preserved. The 3 survivors point at the three live docs kept last sweep (both Q3 plans and the post-event survey). |

### Original question text, for the record

**Product / code decisions**
1. **Campaign unsubscribe is a no-op.** Campaign emails render the footer link *and* RFC 8058
   `List-Unsubscribe` headers, but `CampaignCodes` is in `MessageCategoryExtensions.IsAlwaysOn`, so
   `UpdatePreferenceAsync` ignores the write and `IsOptedOutAsync` always returns false. Dead code
   either way: the opt-out loop in `SendWaveAsync`/`PreviewWaveSendAsync` and the always-zero
   "Unsubscribed (excluded)" preview row.
2. **Wave eligibility has no active/suspended check** — `SendWaveAsync` takes `TeamInfo.Members`
   (`LeftAt == null`). Doc corrected to match code; the code may be what is wrong.
3. **`IssueSectionInference.Map` matches path segment `"city"`** but the route prefix is
   `/CityPlanning` — an issue filed from any City Planning page infers a null Section and lands in
   the Admin queue instead of CampAdmin. Every other mapping matches a real first segment.
4. **`/Holded` has no sidebar entry** in `AdminNavTree.cs` — reachable only by direct URL or the link
   on Finance's `CreditorStatement.cshtml`.
5. **Guide role blocks headed `(Camp Coordinator)`** — not a key in `GuideRolePrivilegeMap.cs`, so
   those blocks are invisible to camp coordinators and reach only Board/Admin.
6. **`/dev/seed/budget` and `/dev/seed/camp-roles` have no button** in any `.cshtml` — unreachable
   actions, which `code-review-rules.md`'s "Orphaned Pages" makes a reject.
7. **`CampaignService.ImportCodesAsync` has no service-level status gate**; only the Detail view
   hides the button outside Draft/Active.

**Architecture boundary calls**
8. **`google_resources` / `TeamResourceService` ownership.** `GoogleResourceRepository` maps the
   table on `GoogleIntegrationDbContext` and the service sits in `Services/GoogleIntegration/`, so
   §8 was corrected to Google Integration to obey *"a table must only exist in one repository"*. If
   Teams should own the Teams-to-Drive link, the **repository and DbContext mapping** move, not the
   doc line.
9. **Hard-rules conflict — Notifications.** §10 lists it as a horizontal crosscut, but
   `NotificationMeterProvider` injects `IProfileService`, `IUserService`, `IGoogleSyncServiceRead`,
   `ITeamService`, `ITicketSyncService`, and `IApplicationDecisionService`.
   `peters-hard-rules.md` says horizontals *"are strictly forbidden from referencing vertical
   sections … as that will cause loops in the call graph."* Pre-existing; not resolved here.
10. **HUM0031's controller thresholds.** *Answered: frozen until 2027, permanently settled — see
    `memory/architecture/hum0031-frozen-until-2027.md`. Never raise this again.*

**Sweep mechanics**
11. **Dangling wheat provenance.** Several G5 docs carry `<!-- wheat: docs/superpowers/specs/… -->`
    markers pointing at spec files deleted before this anchor (Campaigns, CityPlanning, Containers,
    Finance, Issues, Notifications, Store).
12. **Archived specs drift too.** The retained `2026-06-10-table-component-design.md` still gives
    pre-G5 paths, and a live source file cites it. `docs/superpowers/**` is catalog-ignored.
13. **`memory/` is not in `editorial_trees` at all.** Two atoms
    (`datetime-format-single-home.md`, `datetime-display-formatting.md`) name
    `Humans.Web.Extensions.DateTimeDisplayExtensions`; the real namespace is `Humans.UI.Extensions`.

## Out-of-scope drift recorded (not fixed)

- `FeedbackSource.AgentUnresolved` + `AgentConversationId` are unreachable (no creation path since
  #977) and their XML comment cites an agent tool that no longer exists. Dropping needs a migration —
  debt-ledger candidate.
- `CalendarDbContext`'s docstring says migrations live under `Migrations/Calendar/`; they are at
  `src/Sections/Humans.Calendar/Data/Migrations/`.
- `admin-shell.md`'s per-role sidebar matrix omits items present in `AdminNavTree.cs` today
  (Barrios/Compliance, Members/{Account merges, Email problems, Audience segmentation},
  Shifts/Orphan signups, Tickets/Campaigns). The doc says the matrix is pinned by
  `tests/e2e/tests/admin-shell.spec.ts` `sidebarMatrix`, so that test may be stale too.
- `Profiles.md` still carries an `account_merge_requests` data-model block at L239-241 while L411/L427
  correctly state the table moved to Users.
- `_Index.md`'s Mailer row says "— (background sync)" though `MailerAdminController` exists; the
  Calendar row omits `ICalFeedApiController`. Both landed in #931, pre-anchor.

## Skipped (errors)

None.

## Process note

Subagents again went idle without delivering payloads — **fifth consecutive sweep** with this
failure. Worked around with the established file-payload pattern (agents write JSON to a scratchpad
path outside the worktree).

The sharper version of this finding: the payloads are not *lost*, they are **very late**. Every lane
that looked dead eventually reported, but several arrived *after* the sweep had verified their work
from the worktree diff, written the report, committed, and opened the PR. Waiting for them would
have stalled the sweep indefinitely; the diff-verification fallback is what kept it moving, and it
should stay in the procedure alongside the file-payload pattern rather than being treated as an
emergency measure.

Two inter-lane contradictions were caught only because the orchestrator cross-checked lanes against
each other and against source (`ISurveyServiceRead`, `TicketingBudgetService` ownership). Neither
lane flagged its own error. That cross-check is the most valuable step added this sweep and is worth
making explicit in the skill.
