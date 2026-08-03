# Feedback — G0 First Audit

Section: Feedback · Kind: vertical · Audited 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository | PASS | `reforge ownership-violations --owner Feedback --tables feedback_reports,feedback_messages` → `0 ownership-violations`. |
| 2 | One writer-service per table (no interceptor workarounds) | PASS | `reforge injected IFeedbackRepository` → single consumer `FeedbackService` (`src/Humans.Application/Services/Feedback/FeedbackService.cs:30`). |
| 3 | No EF entity leaks across the boundary | PASS (evidence corrected 2026-08-03) | Read methods return `FeedbackReportInfo`/`FeedbackMessageInfo` DTOs with display names pre-resolved — predicate itself still holds (no entity leak). ~~No cross-section consumers listed~~ is wrong: `AgentUserSnapshotProvider` (`src/Humans.Infrastructure/Services/Agent/AgentUserSnapshotProvider.cs:21,39`) injects the full `IFeedbackService` and calls `GetOpenFeedbackIdsForUserAsync` cross-section — a real consumer, just via a DTO-returning method, so no leak. This was missed because the dependency DAG's original pass omitted the `Agent` section entirely (see DAG correction, 2026-08-03) — Feedback does have a cross-section consumer, it just doesn't need a dedicated `IFeedbackServiceRead` yet since the one call is read-shaped through the full interface. |
| 4 | No cross-section EF joins (zero baseline entries) | FAIL | `FeedbackReportConfiguration.cs:9` and `FeedbackMessageConfiguration.cs:8` both carry `[Grandfathered(ruleId: "HUM0024", justification: "Pre-existing cross-section EF navigation join; migrating to bare FK + service-level stitching.", since: "2026-05-25")]` — these ARE modeled cross-section EF navigations at the DB-config level (to `User`/`Team`), even though the app-layer nav properties are `[Obsolete]` and unused by the repository. |
| 5 | No `[Obsolete]` cross-section navs, no `[Grandfathered]`, no owned baseline rows | FAIL (same root cause as #4) | 4 `[Obsolete]` cross-domain navs on `FeedbackReport` (`.User`, `.ResolvedByUser`, `.AssignedToUser`, `.AssignedToTeam`) + 1 on `FeedbackMessage` (`.SenderUser`), plus the 2 `[Grandfathered]` HUM0024 config attributes above. **This is a queued item** — the Grandfathered justification itself states "migrating to bare FK + service-level stitching," i.e. a G2 demolition item exists in intent but is not tracked as a numbered issue/ledger entry yet. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | `grep HUM0031 src/Humans.Web/Controllers/Feedback*.cs` → zero matches. |
| 7 | `docs/sections/Feedback.md` exists and matches reality | PASS | Exists, current, detailed, and explicitly documents the HUM0024 grandfathers and touch-and-clean guidance (§ "Touch-and-clean guidance": "Do not reintroduce `.Include(f => f.User \| ...)`"). Matches code. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests use real Postgres, zero EF-InMemory | FAIL | `tests/Humans.Application.Tests/Repositories/FeedbackRepositoryTests.cs:22` uses `.UseInMemoryDatabase(Guid.NewGuid().ToString())`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | Grepping these files for the literal `HumansDbContext` is a false negative: they inherit their EF setup. `FeedbackServiceTests` extends `ServiceTestHarness` and constructs a concrete repository. `ServiceTestHarness` (`tests/Humans.Application.Tests/Infrastructure/ServiceTestHarness.cs:54,71`) builds a real `HumansDbContext` over `.UseInMemoryDatabase(...)` and exposes it as `Db`/`DbFactory`, so a harness-derived test constructing a concrete repository is exactly the EF-InMemory service test this predicate forbids. Same false-negative pattern corrected across Auth, Budget, Camps, CityPlanning, Feedback, Governance and Consent in this pass. |
| 3 | Invariants/triggers each have a test (spot-check) | PASS (spot-check) | `ResolvedAt`/status-transition-clears-resolution fields covered (lines ~175, ~190 of `FeedbackServiceTests.cs`: resolved-status sets `ResolvedAt`, transitioning out clears it). Not directly verified here: screenshot MIME/size validation test, "needs reply" derivation test, admin-reply-sends-email-before-persist ordering test — would need a fuller read of the 400+-line test file to confirm all are present; flagging as **unverified, not confirmed missing**. |
| 4 | No skipped tests without an issue ref | PASS | `Skip\s*=` grep on `Services/FeedbackServiceTests.cs` and `Repositories/FeedbackRepositoryTests.cs` → no matches. |
| 5 | Tests grouped under the section | FAIL | Unlike Events/Expenses/Finance (which have `Events/`, `Services/Expenses/`, `Finance/` subfolders), Feedback's tests are two loose top-level files: `tests/Humans.Application.Tests/Services/FeedbackServiceTests.cs` and `tests/Humans.Application.Tests/Repositories/FeedbackRepositoryTests.cs` — not grouped under a `Feedback/` subfolder. Not movable as a unit at G5 without a rename/move first. |

## G1 Gap List

1. **HUM0024 cross-section EF joins on `FeedbackReportConfiguration`/`FeedbackMessageConfiguration`** — where: `src/Humans.Infrastructure/Data/Configurations/Feedback/FeedbackReportConfiguration.cs:9`, `FeedbackMessageConfiguration.cs:8`. What: DB-level FK/nav wiring to `User`/`Team` still exists even though app-layer `[Obsolete]` navs aren't read. Suggested fix: drop the EF `HasOne(...).WithMany()` nav-wiring blocks entirely, keep the FK columns as bare scalars (pattern already used by Events/Expenses/Finance), remove the 5 `[Obsolete]` attributes and the 2 `[Grandfathered]` attributes. No-migration-needed: **y** (dropping EF-level nav config, not the DB FK constraint itself — though if the DB-level FK constraint also needs dropping, that's a G2 item, matching the "Great Cleanup" cross-section-FK-drop theme in the transition plan). File as a G2 demolition item if not already tracked.

## G3 Gap List

1. **`FeedbackRepositoryTests.cs` uses `UseInMemoryDatabase`** — where: `tests/Humans.Application.Tests/Repositories/FeedbackRepositoryTests.cs:22`. Suggested fix: convert to the shared Postgres fixture. No-migration-needed: **y**.
2. **Tests not grouped under a `Feedback/` subfolder** — where: `Services/FeedbackServiceTests.cs`, `Repositories/FeedbackRepositoryTests.cs`. Suggested fix: move both into `Services/Feedback/` and `Repositories/Feedback/` (or a top-level `Feedback/` folder matching the Events/Expenses pattern) ahead of G5. No-migration-needed: **y**.


**Added 2026-08-03 — harness-inherited EF-InMemory (G3.2).** `FeedbackServiceTests` extend `ServiceTestHarness`, which stands up a real `HumansDbContext` over `.UseInMemoryDatabase(...)`; the original pass missed this because it grepped for a literal `HumansDbContext` the files never name. Fix: convert to `Substitute.For<IFeedbackRepository>()` per #766, or move these off the harness. No-migration-needed: **y**.

## G2 Queue Notes (light)

- The HUM0024 cross-section-join demolition (G1 gap #1) is this section's clearest G2 candidate — it's explicitly self-documented as in-progress-intent ("migrating to bare FK") but not yet executed. Should be filed as a tracked issue if one doesn't already exist, since the transition plan's demolition inventory expects named items, not just Grandfathered-attribute prose.
- No dead columns spotted otherwise; data model is otherwise lean.


**Added 2026-08-03 — cross-section FK cuts belong in this queue.** Retiring `[Obsolete]` navs or `[Grandfathered(HUM0024)]` markers is a code-shape change; it does **not** drop the physical constraint. Per the demolition inventory, this section owns **5** cross-section FKs across 2 tables: `feedback_reports` → `AspNetUsers` ×3 (`User`/`ResolvedByUser`/`AssignedToUser`) and `teams` ×1 (`AssignedToTeam`), plus `feedback_messages.SenderUserId` → `AspNetUsers`. All are G2 cuts — without them listed here, a schema batch driven by this scorecard can complete while every cross-section database dependency survives.

