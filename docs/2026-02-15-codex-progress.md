# Codex Progress Log (2026-02-15)

## Scope
Execution log for `docs/2026-02-15-codex-plan.md` with checkpoint-based iteration.

## Working Loop
1. Execute one small phase slice.
2. Validate with targeted build/tests where possible.
3. Record decisions, files changed, and blockers here.
4. Commit a checkpoint.

## Current Phase
`Phase 5` (startup modularization) complete

## Decisions
- 2026-02-15: Do **not** add DB exclusion/non-overlap constraint for role windows in Phase 1.
- 2026-02-15: Enforce role overlap prevention in application logic instead.
- 2026-02-15: Do not use "fast repair" shortcuts; use project-consistent, debt-free fixes only.

## Work Log
- 2026-02-15: Created consolidated plan doc `docs/2026-02-15-codex-plan.md`.
- 2026-02-15: Removed reserved-name file `nul` from workspace.
- 2026-02-15: Checkpoint commit created: `2fe3085` (`docs: checkpoint 2026-02-15 recommendations and plan`).
- 2026-02-15: Phase 0 baseline attempted.
  - `dotnet restore/build/test` are blocked by restricted network to NuGet (`NU1301`).
  - `--ignore-failed-sources` is not sufficient in this sandbox because required packages are not fully cached locally (`NU1101`).
  - `dotnet ef migrations list --no-build` works against local artifacts and shows migrations:
    - `20260212152552_Initial`
    - `20260213005525_AddEmergencyContactFields`
    - Pending status unknown because local PostgreSQL is unavailable in this sandbox.
- 2026-02-15: Phase 1 implementation started.
  - Added DB check constraints in model configuration:
    - `CK_google_resources_exactly_one_owner`
    - `CK_role_assignments_valid_window`
  - Added migration: `20260215150500_AddIntegrityCheckConstraints.cs`
  - Added app-layer role overlap guard service:
    - `IRoleAssignmentService`
    - `RoleAssignmentService`
  - Wired overlap guard into admin role assignment flow (`AdminController.AddRole`).
  - Added tests for overlap behavior: `RoleAssignmentServiceTests`.
- 2026-02-15: Checkpoint commit created: `794d2d4` (`phase1: add integrity checks and role overlap guard`).
- 2026-02-15: Phase 2 implementation completed.
  - Added consolidated snapshot DTO: `MembershipSnapshot`.
  - Extended `IMembershipCalculator` with `GetMembershipSnapshotAsync`.
  - Implemented snapshot aggregation in `MembershipCalculator`.
  - Added explicit `IsApproved` gate to membership status computation:
    - `MembershipCalculator.ComputeStatusAsync`
    - `Profile.ComputeMembershipStatus`
  - Updated web consumers to use consolidated snapshot:
    - `HomeController`
    - `ProfileController`
    - `HumanController`
  - Added tests:
    - `MembershipCalculatorTests` (approval gate + snapshot behavior)
    - `ProfileTests` updated with Pending/not-approved coverage.
- 2026-02-15: Checkpoint commit created: `e02db5e` (`phase2: unify membership snapshot and approval gate`).
- 2026-02-15: Phase 3 extraction started (controller slice).
  - Added `AdminLegalDocumentsController` with legal-doc admin actions moved out of `AdminController`.
  - Removed legal-doc actions and legal-doc-specific constructor dependencies from `AdminController`.
  - Kept route paths under `/Admin/...` unchanged via attribute routes.
  - Updated cross-controller links to legal-doc routes:
    - `src/Humans.Web/Views/Admin/Index.cshtml`
    - `src/Humans.Web/Views/Team/Details.cshtml`
- 2026-02-15: Phase 3 service extraction completed for legal-doc slice.
  - Added interface + contracts:
    - `IAdminLegalDocumentService`
    - `AdminLegalDocumentUpsertRequest`
    - `GitHubFolderPathNormalizationResult`
  - Added implementation:
    - `AdminLegalDocumentService`
  - Refactored `AdminLegalDocumentsController` to delegate data/query/update/sync behavior to service.
  - Registered service in DI (`Program.cs`).
  - Added focused tests:
    - `AdminLegalDocumentServiceTests`
- 2026-02-15: Validation pass attempted.
  - `dotnet build Humans.slnx --no-restore` is still blocked by restricted network to NuGet (`NU1301`), even after setting `DOTNET_CLI_HOME` to a writable workspace folder.
- 2026-02-15: Removed local build artifact folder `.dotnet/` from workspace.
- 2026-02-15: Checkpoint commit created: `fd4708d` (`refactor(admin): extract legal docs feature slice service/controller`).
- 2026-02-15: Phase 4 outbox boundary started.
  - Added outbox model/configuration/migration:
    - `GoogleSyncOutboxEvent`
    - `GoogleSyncOutboxEventConfiguration`
    - `20260215193000_AddGoogleSyncOutbox.cs`
  - Updated `HumansDbContext` and model snapshot with `google_sync_outbox`.
  - Updated `TeamService` to enqueue outbox events for add/remove membership changes instead of direct Google sync calls in-request.
  - Added outbox processor job:
    - `ProcessGoogleSyncOutboxJob`
  - Registered/scheduled outbox processing in `Program.cs` (`Cron.Minutely`).
  - Added focused outbox job tests:
    - `ProcessGoogleSyncOutboxJobTests`
- 2026-02-15: Validation pass attempted for Phase 4.
  - `dotnet build Humans.slnx --no-restore` remains blocked by network/package cache limits:
    - `NU1301` (cannot reach NuGet signature endpoint)
    - `NU1101` (required packages/analyzers not available offline)
- 2026-02-15: Removed regenerated local build artifact folder `.dotnet/` from workspace.
- 2026-02-15: Checkpoint commit created: `77ff372` (`feat(sync): add outbox-backed google sync dispatcher`).
- 2026-02-15: Phase 5 startup modularization started (optional).
  - Added service-registration extension:
    - `InfrastructureServiceCollectionExtensions.AddHumansInfrastructure(...)`
  - Added recurring-job scheduling extension:
    - `RecurringJobExtensions.UseHumansRecurringJobs(...)`
  - Updated `Program.cs` to use both extensions and removed inline registration/scheduling blocks.
- 2026-02-15: Validation pass attempted for Phase 5.
  - `dotnet build Humans.slnx --no-restore` remains blocked by `NU1301` (NuGet signature endpoint unreachable in this sandbox).
- 2026-02-15: Removed regenerated local build artifact folder `.dotnet/` from workspace.
- 2026-02-15: Checkpoint commit created: `bd10d27` (`refactor(startup): move infra and recurring jobs to extensions`).
- 2026-02-15: Reworked migration packaging to match project conventions.
  - Removed hand-authored pending migrations that lacked generated designer metadata:
    - `20260215150500_AddIntegrityCheckConstraints.cs`
    - `20260215193000_AddGoogleSyncOutbox.cs`
  - Regenerated a single EF migration with standard `.cs + .Designer.cs + snapshot`:
    - `20260215182339_AddPreProdIntegrityAndGoogleSyncOutbox`
  - Verified with EF tooling:
    - migration is discovered as pending,
    - no pending model changes.

## Next Step
- Apply `20260215182339_AddPreProdIntegrityAndGoogleSyncOutbox` in deployed environments, then smoke-test admin legal-doc and team membership sync flows.
