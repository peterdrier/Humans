# Governance — G0 First Audit

**Section:** Governance · **Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repo in-section | PASS | `reforge ownership-violations --owner Governance --tables applications,application_state_history,board_votes` → 0 violations. |
| 2 | One writer-service per table | PASS | `IApplicationRepository` (impl in `Humans.Infrastructure/Repositories/Governance/`) is the sole DbContext consumer for all three tables; `ApplicationDecisionService` the sole write orchestrator. |
| 3 | No EF entity leaks across boundary | PASS | Cross-domain navs (`Application.User`, `.ReviewedByUser`, `ApplicationStateHistory.ChangedByUser`, `BoardVote.BoardMemberUser`) are fully stripped, not just `[Obsolete]`-marked. Display data stitched via `IUserService.GetByIdsAsync` into DTOs. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | No baseline file for cross-section-EF-join; no Governance rows in any of the 5 existing baseline files (grep for `Governance` across all baselines: no matches). |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / baseline rows | PASS | No baseline rows owned by Governance in any of `ApplicationServiceEntityReadReturns`, `DisplaySortInControllers`, `NoDestructiveMigrationOps`, `NoLinqAtDbLayer`, `NoStartupGuards`. No `[Grandfathered]` in Governance controllers. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | Grep for `Grandfathered` across `Governance*.cs`/`Board*.cs` controllers: no matches. |
| 7 | `docs/sections/Governance.md` current | PASS | Detailed, dated, matches the verified-clean architecture; already documents its own decorator decision and read-side design (no caching decorator, rationale given). |

## G3 predicate table

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests real Postgres, zero EF-InMemory | FAIL | `tests/Humans.Application.Tests/Repositories/ApplicationRepositoryTests.cs:21` calls `.UseInMemoryDatabase(...)`. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | PASS | `tests/Humans.Application.Tests/Services/ApplicationDecisionServiceTests.cs` — no `HumansDbContext` references; mocks the repository interface. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively spot-checked. Key invariants (one-vote-per-board-member unique index, term-expiry-to-odd-year calculation, BoardVote deletion-on-finalize, tier-application never blocks Volunteer onboarding) look plausible given `ApplicationDecisionServiceTests.cs` exists, but no invariant→test line mapping was done in this pass. |
| 4 | No skipped tests without an issue ref | PASS | No `[Fact(Skip` / `[Theory(Skip` anywhere in `tests/`. |
| 5 | Tests grouped under section | PARTIAL | Service test (`ApplicationDecisionServiceTests.cs`) and architecture test (`GovernanceArchitectureTests.cs`) are correctly placed, but the repository test (`ApplicationRepositoryTests.cs`) sits in the shared `tests/.../Repositories/` folder rather than a `tests/.../Governance/` folder — same pattern debt as Issues (see that report). Not movable-with-section as-is at G5. |

## G1 gap list

No G1 gaps found for Governance — this section passed cleanly on every predicate.

## G2 queue notes

None identified this pass. `ReviewStartedAt` field is documented as currently unused (no controller path sets it) — worth a demolition-inventory look (dead column candidate) when Governance enters G2, but needs confirmation it isn't reserved for a near-term feature before dropping.

## Verdict

`G1: met · G3: 2 gaps (+1 PARTIAL) — headline gap: ApplicationRepositoryTests still on EF-InMemory; otherwise the cleanest section audited in this batch`
