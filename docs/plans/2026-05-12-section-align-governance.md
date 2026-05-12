# Section Align - Governance
**Run started:** 2026-05-12 | **Mode:** Existing-section | **Worktree:** `H:\source\humans\.worktrees\section-align-governance`
**Branch:** `align/governance` (off `origin/main` @ `092915124`)
**Canonical section name proposal:** Governance

## Axis 1 - Boundary Integrity
1. Name consistency: Canonical service/DTO/doc namespace is `Governance`, but public web surface is split across `Governance`, `Application`, `OnboardingReview`, and `Board`.
2. Controller existence: `GovernanceController` exists, but governance-owned routes also live in `ApplicationController` and the Board-voting actions on `OnboardingReviewController`. `BoardController.Index` includes governance stats but is broader admin/board dashboard composition.
3. URL surface:

| Current route | Owner today | Section-align disposition |
| --- | --- | --- |
| `GET /Governance` | `GovernanceController.Index` | Canonical. |
| `GET /Governance/Roles` | `GovernanceController.Roles` | Canonical route, but role-assignment ownership likely belongs to Auth/Users follow-up. |
| `GET/POST /Application`, `/Application/Create`, `/Application/Details/{id}`, `/Application/Withdraw/{id}` | `ApplicationController` | Drift. Governance-owned tier application surface should move under `/Governance/Applications/*` or an explicitly approved exception. |
| `GET /Application/Admin`, `/Application/Admin/{id}` | `ApplicationController` | Drift. Admin tier application surface should move under `/Governance/Admin/Applications/*` or an explicitly approved exception. |
| `GET /OnboardingReview/BoardVoting`, `/OnboardingReview/BoardVoting/{id}`, `POST /OnboardingReview/BoardVoting/Vote`, `POST /OnboardingReview/BoardVoting/Finalize` | `OnboardingReviewController` | Drift. Board voting is Governance-owned and should move under `/Governance/BoardVoting/*` or `/Governance/Admin/BoardVoting/*`. |
| `GET /Board` | `BoardController.Index` | Composite Board dashboard. Governance stats are consumed via `IAdminDashboardService`; not a pure Governance page. |

4. Views folder: `Views/Governance`, `Views/Application`, and `Views/OnboardingReview/BoardVoting*.cshtml` all render Governance-owned flows. `Views/Shared/Applications.cshtml`, `_ApplicationsListContent.cshtml`, `_ApplicationResponseSections.cshtml`, and `_ApplicationHistory.cshtml` contain application page fragments and should be evaluated for move/partial scope.
5. ViewModel placement: `GovernanceViewModels.cs`, `ApplicationViewModels.cs`, and `BoardVotingViewModels.cs` are section-specific and OK by file name. `AdminApplication*` and `BoardDashboardViewModel` live in `AdminViewModels.cs`, which is grab-bag drift for the tier application admin surface.
6. Controller-base leak: No Governance-specific helper found on `HumansControllerBase` in this pass.
7. Extensions placement: `GovernanceSectionExtensions.cs` exists, but `IMembershipCalculator` and `IMembershipQuery` are registered from `UsersSectionExtensions.cs`. That is Governance service wiring living in the Users section extension.
8. Role surface: Governance consumes global roles (`Board`, `Admin`, `HumanAdmin`) rather than a section-scoped `GovernanceAdmin`. Current semantics may be intentional, but it is a convention exception that should be signed off rather than treated as aligned by default.
9. Inbound cross-section DB access: `HumansMetricsService` reads `db.Applications` directly for approved and submitted counts. Governance already exposes `GetAdminStatsAsync` / `GetPendingApplicationCountAsync`, so the producer API exists; the call site belongs to the metrics/cross-cutting follow-up.
10. Inbound EF navs: Only aggregate-local navs found: `Application.StateHistory`, `Application.BoardVotes`, and inverse `BoardVote.Application` / `ApplicationStateHistory.Application`.
11. Outbound cross-section access: Governance repositories do not read other sections' DbSets or include other-section navs. Application service stitches users/profile/roles/notifications through public service interfaces.
12. Controller to DbContext: No Governance controller injects `HumansDbContext`.
13. Migrations: Existing governance tables appear in historical EF migrations and model snapshot; no new migration work in this run.
14. Invariant doc: `docs/sections/Governance.md` exists and is detailed, but it currently documents split routing as reality. It should be updated after the route decision and should remove any "grandfathered" framing that conflicts with the zero-tolerance boundary rule.
15. Prior review items: Not PR mode; none checked.

## Axis 2 - Internal Cohesion
1. EF leakage from service: `ApplicationDecisionService`, `MembershipCalculator`, and `MembershipQuery` do not import EF or take DbContext. Only XML/comment mentions matched.
2. Caching placement: Governance services do not take `IMemoryCache`; they invalidate nav/notification/voting caches through narrow invalidator interfaces. No repository/controller cache found.
3. DI lifetimes: `IApplicationRepository` is registered scoped and its implementation takes `HumansDbContext`. Current section-align standard wants repositories sealed, factory-based, and Singleton. `ApplicationRepository` is sealed but not factory-based or Singleton.
4. Repository pattern: `ApplicationRepository` lives under `Infrastructure/Repositories/Governance` and is sealed. It still injects `HumansDbContext` directly and includes update/delete-like methods for non-append-only `Application` / transient `BoardVote`; `ApplicationStateHistory` append-only is aggregate-owned.
5. ViewComponent presence + reuse: Governance data is rendered by `NavBadgesViewComponent`, `ThingsToDoViewComponent`, `ProfileCardViewComponent`, and `AdminNavTree` through public Governance interfaces. No Governance-owned view component found for application or board-voting fragments; shared partials should be reviewed during the route/view move.
6. Interface budget + segregation: `IApplicationDecisionService` has 28 `Task` members and no `[SurfaceBudget]`; `IMembershipCalculator` has 12 and no `[SurfaceBudget]`; `IApplicationRepository` has 28 and no `[SurfaceBudget]`. These need budget ratchets or trimming candidates.
7. Architecture test coverage: `GovernanceArchitectureTests` covers service namespace, no DbContext/IMemoryCache on `ApplicationDecisionService`, no EF reference from Application assembly, and repository interface namespace. Missing coverage includes only-Governance-repository touches DbSets, repository factory/singleton shape, route/viewmodel placement, and Governance service registrations.

## Axis 3 - Test Focus
1. Test folder placement: Governance tests are scattered under `tests/Humans.Application.Tests/Services`, `Repositories`, `Architecture`, `Domain.Tests/Entities`, and controller/profile tests. Canonical target should be `tests/Humans.Application.Tests/Governance/` or `tests/Humans.Application.Tests/Services/Governance/`; pick one before moving files.
1a. 1-to-1 map:

| Production class | Current test signal |
| --- | --- |
| `ApplicationDecisionService` | `Services/ApplicationDecisionServiceTests.cs` exists. |
| `ApplicationRepository` | `Repositories/ApplicationRepositoryTests.cs` exists. |
| `MembershipCalculator` | `Services/MembershipCalculatorTests.cs` and `Services/MembershipPartitionTests.cs` exist. |
| `MembershipQuery` | No direct behavior test; may be acceptable as thin pass-through, but should be documented. |
| `GovernanceController` / `ApplicationController` / Board-voting actions | No focused controller tests found in this pass. |

2. Coverage map: Strong service/repository tests exist for application decisions, board-vote cleanup, repository operations, and membership partitioning. Gaps remain for route access / canonical route behavior, "regular human cannot view others/cast votes/manage roles" controller-level negatives, and admin-vs-board finalize UI/endpoint split.
3. Redundancy flags: Not enough signal for deletion without a deeper Phase 3 pass. Existing service tests directly exercise behavior and repository outcomes.
4. Test-to-section ratio: Governance service/repo LOC is about 1,422; broad matching test LOC is about 2,135. Ratio is plausible, but scattering makes maintenance harder.
5. Brittleness signals: Existing service tests use EF InMemory and mocks; no immediate brittle private-reflection or order dependency surfaced in Phase 0.
6. Mutation signal (Stryker.NET): No Governance-specific Stryker config/report found. Existing test gate baseline is `Test attributes: 2139`; this run currently adds no tests.

## Test-Attribute Gate
- Baseline as of last gate update: 2139
- This run's net delta: +0 / -0 = 0
- Justification if net > 0: Not applicable for Phase 0.

## Stop Conditions Tripped
1. Route/canonical-surface decision: Governance-owned flows currently live at `/Application/*` and `/OnboardingReview/BoardVoting*`. Moving them to `/<Section>/*` is user-visible and broad. Decision needed:
   - A: Full canonical move to `/Governance/Applications/*`, `/Governance/Admin/Applications/*`, and `/Governance/BoardVoting/*`, updating nav/docs/notifications/tests and deciding whether to keep temporary redirects.
   - B: Keep `/Application/*` and `/OnboardingReview/BoardVoting*` as approved exceptions and document why Governance is not following the route convention here.
   - C: Phase in canonical routes first with legacy redirects, then remove legacy routes in a later release.
2. Role ownership decision: `/Governance/Roles` renders role assignments through Auth/role-assignment services. Decide whether this remains Governance UI composition or moves to an Auth/Users section pass.
3. Repository standard decision: Moving `ApplicationRepository` to `IDbContextFactory<HumansDbContext>` + Singleton is mechanical but touches tests and DI. This can proceed in Phase 2 if the route decision does not block.

## Follow-up /section-align Targets
- Metrics / Observability: `HumansMetricsService` reads `db.Applications` directly despite existing Governance service APIs.
- Auth / Users: Role assignment browse/manage UI and service ownership are mixed into `GovernanceController.Roles`; the role surface needs its own owner decision.
- Onboarding: Board-voting routes/views are still hosted in `OnboardingReviewController` and `Views/OnboardingReview`.
- AuditLog / Administration: `/Board` dashboard and `/Board/AuditLog` are composite/global surfaces that overlap Governance docs and navigation.

## Phase Plan
- Phase 1 (axis 1 + route/view/test mechanical): after user decision, move or explicitly document `/Application/*` and `/OnboardingReview/BoardVoting*`; move application admin ViewModels out of `AdminViewModels.cs`; move Governance service registrations out of `UsersSectionExtensions.cs`; update route references in docs/resources/nav/tests.
- Phase 2 (arch + boundary fixes): convert `ApplicationRepository` to `IDbContextFactory<HumansDbContext>` + Singleton; add/update architecture tests for repository shape and only-repository DbSet access; add `[SurfaceBudget]` ratchets or trim candidates for Governance interfaces; update `HumansMetricsService` to use Governance APIs if treating metrics as in-scope, otherwise leave as follow-up.
- Phase 3 (simplify / tests): consolidate scattered Governance tests under a canonical folder, split or document bundled test files, add focused negative/controller coverage, then prune any redundant shape tests found by inspection.
- Phase 4 (docs): update `docs/sections/Governance.md`, `docs/guide/Governance.md`, governance feature docs, dependency graph, data model, and any admin/global docs affected by the route decision.
