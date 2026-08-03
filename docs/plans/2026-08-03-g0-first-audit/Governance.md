# Governance — G0 First Audit

**Section:** Governance · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | PASS | `reforge ownership-violations --owner Governance --tables applications,application_state_history,board_votes` → 0 violations. |
| 2 | One writer-service per table | PASS | `IApplicationRepository` (impl in `Humans.Infrastructure/Repositories/Governance/`) is the sole DbContext consumer for all three tables; `ApplicationDecisionService` the sole write orchestrator. |
| 3 | No EF entity leaks across boundary | PASS | Cross-domain navs (`Application.User`, `.ReviewedByUser`, `ApplicationStateHistory.ChangedByUser`, `BoardVote.BoardMemberUser`) are fully stripped, not just `[Obsolete]`-marked. Display data stitched via `IUserService.GetByIdsAsync` into DTOs. |
| 4 | No cross-section EF joins (zero baseline entries) | **FAIL — corrected 2026-08-03** | The baseline greps are correct but can't establish a pass — HUM0024 is attribute-allowlisted. Governance owns **four** cross-section relationships across three tables, all typed `HasOne<User>()` → `AspNetUsers`: `ApplicationConfiguration.cs:49-51` (`ReviewedByUserId`) and `:56-58` (`UserId`); `ApplicationStateHistoryConfiguration.cs:27-29` (`ChangedByUserId`); `BoardVoteConfiguration.cs:32-34` (`BoardMemberUserId`). Unusually, **none carries a `[Grandfathered(HUM0024)]` marker** — so unlike most sections these are outside the allowlist entirely (same anomaly as GoogleIntegration). The demolition inventory recorded Governance as having zero cross-section FKs, which was wrong and is corrected in the same commit. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | PASS | No baseline rows owned by Governance in any of `ApplicationServiceEntityReadReturns`, `DisplaySortInControllers`, `NoDestructiveMigrationOps`, `NoLinqAtDbLayer`, `NoStartupGuards`. No `[Grandfathered]` in Governance controllers. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | Grep for `Grandfathered` across `Governance*.cs`/`Board*.cs` controllers: no matches. |
| 7 | `docs/sections/Governance.md` current | PASS | Detailed, dated, matches the verified-clean architecture; already documents its own decorator decision and read-side design (no caching decorator, rationale given). |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | FAIL | `tests/Humans.Application.Tests/Repositories/ApplicationRepositoryTests.cs:21` calls `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | **FAIL — corrected 2026-08-03** | Grepping these files for the literal `HumansDbContext` is a false negative: they inherit their EF setup. `ApplicationDecisionServiceTests` extends `ServiceTestHarness` and constructs a concrete repository rather than mocking the interface. `ServiceTestHarness` (`tests/Humans.Application.Tests/Infrastructure/ServiceTestHarness.cs:54,71`) builds a real `HumansDbContext` over `.UseInMemoryDatabase(...)` and exposes it as `Db`/`DbFactory`, so a harness-derived test constructing a concrete repository is exactly the EF-InMemory service test this predicate forbids. Same false-negative pattern corrected across Auth, Budget, Camps, CityPlanning, Feedback, Governance and Consent in this pass. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively spot-checked. Key invariants (one-vote-per-board-member unique index, term-expiry-to-odd-year calculation, BoardVote deletion-on-finalize, tier-application never blocks Volunteer onboarding) look plausible given `ApplicationDecisionServiceTests.cs` exists, but no invariant→test line mapping was done in this pass. |
| 4 | No skipped tests without an issue ref | PASS | No `[Fact(Skip` / `[Theory(Skip` anywhere in `tests/`. |
| 5 | Tests grouped under section | PARTIAL | Service test (`ApplicationDecisionServiceTests.cs`) and architecture test (`GovernanceArchitectureTests.cs`) are correctly placed, but the repository test (`ApplicationRepositoryTests.cs`) sits in the shared `tests/.../Repositories/` folder rather than a `tests/.../Governance/` folder — same pattern debt as Issues (see that report). Not movable-with-section as-is at G5. |

## G1 gap list

**Added 2026-08-03 (was: "no G1 gaps found").** 4 un-grandfathered cross-section FKs — `ApplicationConfiguration.cs:49,56`, `ApplicationStateHistoryConfiguration.cs:27`, `BoardVoteConfiguration.cs:32`, all → `AspNetUsers` across `applications`, `application_state_history` and `board_votes` (see predicate 4). None carries `[Grandfathered(HUM0024)]`, so they're invisible to the attribute allowlist the rest of this inventory relies on — worth understanding why the analyzer doesn't fire before the cuts are queued. Counted as one G1 item; the four FK cuts themselves are G2. No-migration-needed: **y** (investigation); FK cuts are G2.

Every other G1 predicate passed cleanly — Governance remains among the cleanest sections in the batch on ownership and writer discipline.


**Added 2026-08-03 — harness-inherited EF-InMemory (G3.2).** `ApplicationDecisionServiceTests` extend `ServiceTestHarness`, which stands up a real `HumansDbContext` over `.UseInMemoryDatabase(...)`; the original pass missed this because it grepped for a literal `HumansDbContext` the files never name. Fix: convert to `Substitute.For<IApplicationRepository>()` per #766, or move these off the harness. No-migration-needed: **y**.

## G2 queue notes

**Corrected 2026-08-03 — "none identified" contradicted this scorecard's own G1 gap list and the demolition inventory.** Governance's G2 queue is the **four cross-section FK cuts** to `AspNetUsers`: `applications.ReviewedByUserId` and `applications.UserId` (`ApplicationConfiguration.cs:49,56`), `application_state_history.ChangedByUserId` (`ApplicationStateHistoryConfiguration.cs:27`), and `board_votes.BoardMemberUserId` (`BoardVoteConfiguration.cs:32`). Governance must not advance through G2 while those constraints remain.

Also queued: the three table renames (`applications` → `governance_applications`, `application_state_history` → `governance_application_state_history`, `board_votes` → `governance_board_votes`) per the inventory. Separately, `ReviewStartedAt` is documented as currently unused (no controller path sets it) — a dead-column candidate worth confirming isn't reserved for a near-term feature before dropping.

## Headline

ApplicationRepositoryTests is still on EF-InMemory; otherwise the cleanest section audited in this batch.
