# Onboarding — G0 First Audit

**Section:** Onboarding · **Kind:** vertical (orchestrator) · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository in-section | N/A | Orchestrator — owns no tables (confirmed: `OnboardingService` injects no `I*Repository`, no `HumansDbContext`, no `IDbContextFactory` — enforced by `OnboardingArchitectureTests`). |
| 2 | One writer-service per table | N/A | Same as above. |
| 3 | No EF entity leaks across boundary | PASS | `OnboardingService`'s methods do not appear in `ApplicationServiceEntityReadReturns.baseline.txt`. It writes through owning-section services (`IUserService`, `IApplicationDecisionService`, `ISystemTeamSync`) only. |
| 4 | No cross-section EF joins | PASS | No repository access at all; `CrossSectionEfJoinAnalyzer` has nothing to flag. |
| 5 | No `[Obsolete]` cross-section navs / `[Grandfathered]` / owned baseline rows | PASS | `grep` for `Grandfathered` in `OnboardingService.cs` and `OnboardingReviewController.cs`: zero hits. Owns no entities, so no navs. |
| 6 | Controllers thin (no HUM0031 grandfathers) | PASS | `OnboardingReviewController` is not among the 8 controllers carrying `[Grandfathered(ruleId: "HUM0031", …)]` (`AccountController`, `EmailController`, `EventsController`, `ProfileController`, `ShiftsController`, `TeamAdminController`, `TeamController`, `UsersAdminDebugController`). Note: `AccountController` and `MembershipRequiredFilter`/`NameRequiredFilter` are also routed through this section's doc table, but `AccountController`'s HUM0031 grandfather is scored under **Users** (it owns that controller) to avoid double-counting. |
| 7 | `docs/sections/Onboarding.md` current | PASS (high confidence) | Doc reflects the nobodies-collective#584 narrowing (three-concerns split), the peer-call consent-check threshold, and the `NameRequiredFilter` gate — all consistent with current `OnboardingService.cs` shape (interface-only ctor, no DbContext/repo). |

**IOrchestrator marker check** (per `memory/architecture/orchestrator-marker.md`): `IOnboardingService : IOrchestrator` confirmed at `src/Humans.Application/Interfaces/Onboarding/IOnboardingService.cs:17`. Sibling `IHumanLifecycleService : IOrchestrator` also confirmed. Both correctly carry the sibling marker, not `IApplicationService`.

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repo tests on real Postgres, zero EF-InMemory | N/A | No owned tables, no repository. |
| 2 | Service tests mock interfaces, zero `HumansDbContext` | PASS | `tests/Humans.Application.Tests/Services/Onboarding/OnboardingServiceTests.cs` and `OnboardingWidgetStateTests.cs` — grep for `ServiceTestHarness`/`HumansDbContext`/`UseInMemoryDatabase` returns zero hits; deps are mocked interfaces. |
| 3 | Invariants/triggers each have a test | PARTIAL | Not exhaustively spot-checked line-by-line against every bullet in the doc's Invariants/Triggers sections (11 invariants, 5 triggers). The consent-check peer-call threshold and name-gate are plausible test targets given the file exists and is substantial, but no per-invariant traceability was verified this pass. |
| 4 | No skipped tests without an issue ref | PASS (tentative) | Repo-wide `Skip\s*=\s*"` grep found only one hit, in `LocalizationCoverageSweep.cs` (unrelated section). None in Onboarding test files. |
| 5 | Tests grouped under section | PASS | Live under `tests/Humans.Application.Tests/Services/Onboarding/`. |

## G1 gap list

None — no gaps found this pass.

## G3 gap list

| What | Where | Suggested fix | No-migration-needed? |
|------|-------|----------------|----|
| Invariant→test traceability not verified exhaustively | `tests/Humans.Application.Tests/Services/Onboarding/*` vs `docs/sections/Onboarding.md` Invariants/Triggers | A future audit pass (or `/section-gate advance`) should line up each of the 11 invariants / 5 triggers against a named test. | y |

## G2 queue notes

Owns no tables — G2 (schema) is trivially satisfied once its dependent sections (Profiles/Users, Legal & Consent, Teams, Governance) clear their own G2 items. No demolition-inventory items originate here.
