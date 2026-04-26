# Camp roles — rebase port plan (nobodies-collective#489)

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the camp role-assignments feature (currently on `sprint/2026-04-24/issue-489` HEAD `46bf7332`) onto current `peterdrier/Humans:main`, which has landed PR #228 (CampMember) and the §15 Application-layer/repository refactor for the Camps section.

**Source branch (read-only):** `sprint/2026-04-24/issue-489` at `46bf7332`.
**Target base:** `origin/main` (peterdrier/Humans).
**New branch:** `sprint/2026-04-26/issue-489-rebased`.
**Open PR for replacement:** [peterdrier/Humans#335](https://github.com/peterdrier/Humans/pull/335) — close once the rebased branch's PR opens.

## Key delta on main since we forked

1. **CampService moved.** `src/Humans.Infrastructure/Services/CampService.cs` → `src/Humans.Application/Services/Camps/CampService.cs`. Now goes through `ICampRepository`. Never imports EF Core. Verified at `git ls-tree -r origin/main --name-only | grep CampService`.
2. **Application interfaces are namespace-folder organised.** `Humans.Application.Interfaces` is now subdivided: `.AuditLog`, `.Caching`, `.Camps`, `.Gdpr`, `.GoogleIntegration`, `.Notifications`, `.Repositories`, `.Users`, `.CitiPlanning`. Every `using Humans.Application.Interfaces;` from our branch needs to be replaced with one or more of the targeted namespaces.
3. **`INotificationService` → `INotificationEmitter` in the service layer.** Source-branch decision was "controller fires notifications" (matching PR #228). Main's new pattern injects `INotificationEmitter` directly into `CampService`. **Re-wire our notifications back into the service** (drop the controller-side fire-and-log calls).
4. **CampService dependencies on main:** `(ICampRepository, IUserService, IAuditLogService, ISystemTeamSync, ICampImageStorage, INotificationEmitter, ICampLeadJoinRequestsBadgeCacheInvalidator, IClock, IMemoryCache, ILogger<CampService>)`.
5. **Repositories live in `Humans.Infrastructure/Repositories/<Section>/`.** `CampRepository` is at `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs`.
6. **Test patterns shift.** With repository in place, service tests likely use `ICampRepository` mocks via NSubstitute rather than in-memory DbContext. Verify by reading the new `tests/Humans.Application.Tests/Services/CampServiceTests.cs` on main before adapting our `CampRoleTests.cs`.
7. **PR #228 (`CampMember`) is fully merged into main.** Our domain entity, EF config, and migration changes for `CampMember` are duplicates that need to be skipped — keep main's versions.

## Files to port (additive only)

These exist on our branch and need to land on the rebased branch. Mark each as **new** (no main equivalent), **modify** (extend a main file), or **drop** (already in main).

| Path | Status | Notes |
|---|---|---|
| `src/Humans.Domain/Entities/CampRoleDefinition.cs` | **new** | No conflict — copy verbatim. |
| `src/Humans.Domain/Entities/CampRoleAssignment.cs` | **new** | Copy verbatim. The `User AssignedByUser` nav was added on our branch. |
| `src/Humans.Domain/Entities/CampMember.cs` | **modify** | Add only the `RoleAssignments` nav collection; rest is on main. |
| `src/Humans.Domain/Entities/CampSeason.cs` | **modify** | Add only the `RoleAssignments` nav collection. |
| `src/Humans.Domain/Enums/AuditAction.cs` | **modify** | Append the 6 `CampRoleDefinition*` / `CampRole*` values. Beware: PR #228 already added `CampMember*` values — main's enum positions may differ from ours. |
| `src/Humans.Domain/Enums/NotificationSource.cs` | **modify** | Append `CampRoleAssigned`. Use the next int after main's max value. |
| `src/Humans.Application/NotificationSourceMapping.cs` | **modify** | Add the `CampRoleAssigned => MessageCategory.TeamUpdates` arm. |
| `src/Humans.Application/Interfaces/Camps/ICampService.cs` | **modify** | Append role-related method signatures + DTO records (`CampRoleDefinitionDto`, `CampRoleAssignmentDto`, `AssignCampRoleResult`, `AssignCampRoleOutcome`, `CampComplianceRow`). |
| `src/Humans.Application/Interfaces/Repositories/ICampRepository.cs` | **modify** | Append camp-role read/write methods (see §"Repository surface" below). |
| `src/Humans.Application/Services/Camps/CampService.cs` | **modify** | Append the inline role-related implementations, adapted to use `_repo` (not `_dbContext`) and `_notificationEmitter` (not the controller). Modify existing `AddLeadAsync` to auto-link CampMember; modify `RemoveCampMemberAsync` / `RejectSeasonAsync` / `WithdrawSeasonAsync` for cascade. |
| `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs` | **modify** | Append the role-related repo methods that ICampRepository declares. |
| `src/Humans.Infrastructure/Data/Configurations/CampRoleDefinitionConfiguration.cs` | **new** | Copy verbatim. |
| `src/Humans.Infrastructure/Data/Configurations/CampRoleAssignmentConfiguration.cs` | **new** | Copy verbatim. |
| `src/Humans.Infrastructure/Data/HumansDbContext.cs` | **modify** | Add 2 DbSets. |
| `src/Humans.Infrastructure/Migrations/<timestamp>_AddCampRoles.cs` | **regenerate** | Do NOT cherry-pick the old migration. After landing the configs + DbSets above, run `dotnet ef migrations add AddCampRoles` to generate fresh against the new snapshot. EF will emit only what's missing. |
| `src/Humans.Web/Controllers/CampAdminController.cs` | **modify** | Append `Roles`, `CreateRole`, `EditRole`, `DeactivateRole`, `ReactivateRole`, `Compliance` actions. Drop the old GET form actions (we moved to inline editing on Roles.cshtml). Imports use the new namespace folders. |
| `src/Humans.Web/Controllers/CampController.cs` | **modify** | Append `AssignRole`, `UnassignRole`, `BuildRolesPanelAsync` helper. Wire `RolesPanel` into `Edit` GET and `Details`. **DROP** the controller-side notification fire — service emits via `INotificationEmitter` now. |
| `src/Humans.Web/Models/CampViewModels.cs` | **modify** | Append role view models, plus `RolesPanel` property on `CampDetailViewModel` and `CampEditViewModel`. |
| `src/Humans.Web/Views/CampAdmin/Roles.cshtml` | **new** | Copy verbatim from our branch (Teams-mirrored card-per-role inline edit). |
| `src/Humans.Web/Views/CampAdmin/Compliance.cshtml` | **new** | Copy verbatim. |
| `src/Humans.Web/Views/CampAdmin/Index.cshtml` | **modify** | Add the two tile-cards linking to Roles and Compliance. |
| `src/Humans.Web/Views/Camp/_CampRoleSection.cshtml` | **new** | Copy verbatim. |
| `src/Humans.Web/Views/Camp/Edit.cshtml` | **modify** | Add the Roles card section + the auto-promote modal hoist at end of file + `id="roles"` anchor. |
| `src/Humans.Web/Views/Camp/Details.cshtml` | **modify** | Add the `_CampRoleSection` partial render (authenticated only). |
| `src/Humans.Web/Views/Shared/_AutoPromoteMemberModal.cshtml` | **new** | Copy verbatim. |
| `src/Humans.Web/Views/Shared/_HumanSearchInput.cshtml` | **modify** | Add the `IdSuffix` ViewData parameter so the picker can render multiple instances per page. |
| `tests/Humans.Application.Tests/Services/CampRoleTests.cs` | **new** | Adapt fixture to main's `CampService` constructor (now takes `ICampRepository` + the other new deps). Probably need NSubstitute mocks instead of in-memory DbContext. |
| `docs/features/20-camps.md` | **modify** | Append the role-assignment subsection. |
| `docs/sections/Camps.md` | **modify** | Append the role-related invariants. |
| `.claude/DATA_MODEL.md` | **modify** | Add the two new entities to the Key Entities table. |
| `Directory.Packages.props` | **drop** | Main has already moved past 10.0.7 / fixed audits in different ways. Don't carry our old versions; use main's. |
| `docs/superpowers/specs/2026-04-24-camp-roles-design.md` | **carry** | Keep as the design source of truth. Already references the §15 path note. |
| `docs/superpowers/plans/2026-04-24-camp-roles-plan.md` | **carry** | Same. |

## Repository surface

Add to `ICampRepository` (matching the existing read/mutate split with `AsNoTracking` for reads, `IDbContextFactory<HumansDbContext>` for writes):

```csharp
// Camp role definitions (global, CampAdmin-managed)
Task<IReadOnlyList<CampRoleDefinition>> GetRoleDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default);
Task<CampRoleDefinition?> GetRoleDefinitionByIdAsync(Guid id, CancellationToken ct = default);
Task<bool> RoleDefinitionNameExistsAsync(string name, Guid? excludeId, CancellationToken ct = default);
Task AddRoleDefinitionAsync(CampRoleDefinition def, CancellationToken ct = default);
Task UpdateRoleDefinitionAsync(Guid id, Action<CampRoleDefinition> mutate, CancellationToken ct = default);

// Per-slot assignments
Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsForSeasonAsync(Guid campSeasonId, CancellationToken ct = default);
Task<CampRoleAssignment?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default);
Task<bool> SlotIsOccupiedAsync(Guid campSeasonId, Guid roleDefId, int slotIndex, CancellationToken ct = default);
Task<bool> MemberHoldsRoleAsync(Guid campSeasonId, Guid roleDefId, Guid campMemberId, CancellationToken ct = default);
Task AssignSlotAsync(CampRoleAssignment assignment, CampMember? newOrPromotedMember, CancellationToken ct = default);
Task RemoveAssignmentAsync(Guid assignmentId, CancellationToken ct = default);
Task<IReadOnlyList<CampRoleAssignment>> GetAssignmentsByMemberAsync(Guid campMemberId, CancellationToken ct = default);
Task RemoveAssignmentsByMemberAsync(Guid campMemberId, CancellationToken ct = default);
Task RemoveAssignmentsBySeasonAsync(Guid campSeasonId, CancellationToken ct = default);

// Compliance report
Task<IReadOnlyList<(CampSeason Season, Camp Camp, IReadOnlyDictionary<Guid, int> RoleCounts)>>
    GetComplianceDataForYearAsync(int year, CancellationToken ct = default);
```

The service (in `Humans.Application.Services.Camps.CampService`) translates these into the existing `AssignCampRoleAsync` / `UnassignCampRoleAsync` / `GetCampRoleComplianceAsync` shapes.

## Service layer notification rewire

Source branch (HEAD `46bf7332`):
- Service stores enough data in `AssignCampRoleResult` (assignee user id, role name, camp slug, camp name).
- Controller fires `_notificationService.SendAsync(NotificationSource.CampRoleAssigned, ...)` and conditionally `CampMembershipApproved` for auto-promote.

Rebased branch:
- Service injects `INotificationEmitter`. Inside `AssignCampRoleAsync`, after the assignment is persisted (and inside the same transaction):
  ```csharp
  await _notificationEmitter.EnqueueAsync(
      NotificationSource.CampRoleAssigned,
      NotificationClass.Informational,
      NotificationPriority.Normal,
      $"You're now {def.Name} for {season.Name}",
      [member.UserId],
      body: $"You've been assigned as {def.Name} for the current season at {season.Name}.",
      actionUrl: $"/Camps/{season.Camp.Slug}",
      actionLabel: "View barrio",
      ct);
  ```
  And for `Outcome == AssignedWithAutoPromote`, also `EnqueueAsync(CampMembershipApproved, ...)`.

> Verify the exact `INotificationEmitter` method name and parameter list against `src/Humans.Application/Interfaces/Notifications/INotificationEmitter.cs` on main before coding.

Drop the `try/catch _logger` blocks from `CampController.AssignRole` (no longer needed — emitter handles delivery internally).

## Step-by-step

### Phase A — Branch + skeleton

- [ ] Create branch from latest main:
  ```bash
  git fetch origin main
  git checkout -b sprint/2026-04-26/issue-489-rebased origin/main
  ```
- [ ] Verify clean baseline: `dotnet build Humans.slnx` and `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj` both green before any changes.

### Phase B — Domain primitives

- [ ] Add `CampRoleDefinition.cs`, `CampRoleAssignment.cs` (verbatim copy from `git show 46bf7332:src/Humans.Domain/Entities/CampRoleDefinition.cs`).
- [ ] Add nav `RoleAssignments` to `CampMember.cs` and `CampSeason.cs`.
- [ ] Append the 6 `AuditAction` values + `NotificationSource.CampRoleAssigned`. Use whatever int comes next on main.
- [ ] Add the `NotificationSource.CampRoleAssigned => MessageCategory.TeamUpdates` arm to `NotificationSourceMapping.cs`.
- [ ] Build domain + application projects clean.
- [ ] **Commit:** "Add camp role domain entities, enums, and notification mapping".

### Phase C — Persistence (configs, DbSets, migration)

- [ ] Copy `CampRoleDefinitionConfiguration.cs` and `CampRoleAssignmentConfiguration.cs` verbatim into `src/Humans.Infrastructure/Data/Configurations/`.
- [ ] Add 2 DbSets to `HumansDbContext`.
- [ ] Run `dotnet ef migrations add AddCampRoles --project src/Humans.Infrastructure --startup-project src/Humans.Web` — this regenerates a fresh migration against the new snapshot. Do NOT carry the old migration file over.
- [ ] Apply locally and verify the 6 seed roles are present.
- [ ] Run the EF migration reviewer (`.claude/agents/ef-migration-reviewer.md`).
- [ ] **Commit:** "Add EF configs, DbSets, and migration for camp roles".

### Phase D — Repository surface

- [ ] Append the role-related methods (see §"Repository surface" above) to `ICampRepository`.
- [ ] Implement them in `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs`. Reads use `IDbContextFactory<HumansDbContext>().CreateDbContext()` + `AsNoTracking`; mutations use a fresh tracked context + `SaveChangesAsync`. Match the file's existing style.
- [ ] **Commit:** "Add ICampRepository methods for camp roles".

### Phase E — Service layer

- [ ] Append role DTOs/records to `ICampService`.
- [ ] Append role method signatures.
- [ ] In `CampService.cs` (Application), implement role-def CRUD, slot assign with auto-promote, unassign, cascades, compliance — using `_repo` for data access, `_auditLog` for audit, `_notificationEmitter` for in-app notifications.
- [ ] Modify existing `AddLeadAsync` to auto-link the new lead as Active CampMember of the open season (was on our branch — port logic).
- [ ] Modify existing `RemoveCampMemberAsync`, `RejectSeasonAsync`, `WithdrawSeasonAsync` to cascade-delete role assignments via repo.
- [ ] **Commit:** "Implement camp role service methods (CRUD, assign, cascades, compliance)".

### Phase F — Tests

- [ ] Read the existing `tests/Humans.Application.Tests/Services/CampServiceTests.cs` on main to learn the new fixture pattern (likely NSubstitute on `ICampRepository`).
- [ ] Port `CampRoleTests.cs` to that pattern. The behavior assertions stay the same; only the seeding/fixture wiring changes.
- [ ] Run filtered: `dotnet test --filter "FullyQualifiedName~CampRoleTests"` — expect 24 passing tests.
- [ ] Run full Application.Tests suite — expect green.
- [ ] **Commit:** "Add camp role tests adapted to repository fixture pattern".

### Phase G — Web layer

- [ ] Append view models to `CampViewModels.cs` (8 view models from §"Files to port" / Task 3.1 of the original plan). Add `RolesPanel` property to `CampDetailViewModel` and `CampEditViewModel`.
- [ ] Append role-admin actions to `CampAdminController` (using `SetSuccess`/`SetError` TempData feedback — no separate form view).
- [ ] Append `AssignRole`, `UnassignRole`, `BuildRolesPanelAsync` to `CampController`. **No** controller-side notification fires (service emits).
- [ ] Wire `RolesPanel` into `Edit` GET and `Details` (authenticated only).
- [ ] Add `IdSuffix` to `_HumanSearchInput.cshtml`.
- [ ] Copy `_CampRoleSection.cshtml`, `_AutoPromoteMemberModal.cshtml`, `CampAdmin/Roles.cshtml`, `CampAdmin/Compliance.cshtml` verbatim from source branch.
- [ ] Add the Roles card + modal hoist + `id="roles"` to `Camp/Edit.cshtml`.
- [ ] Add the `_CampRoleSection` partial render to `Camp/Details.cshtml`.
- [ ] Add the two tile-cards to `CampAdmin/Index.cshtml`.
- [ ] Build clean.
- [ ] **Commit:** "Port camp role web layer (controllers, view models, views)".

### Phase H — Docs

- [ ] Update `docs/features/20-camps.md`, `docs/sections/Camps.md`, `.claude/DATA_MODEL.md` (verbatim ports — no namespace deltas).
- [ ] **Commit:** "Update camp docs for role assignment feature".

### Phase I — Verification + push

- [ ] `dotnet build Humans.slnx -v q -clp:ErrorsOnly` — clean.
- [ ] `dotnet format Humans.slnx --verify-no-changes` — clean.
- [ ] `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj` — green.
- [ ] Local run + manual smoke (admin manages roles, lead assigns slot, auto-promote modal, compliance report renders).
- [ ] Push branch: `git push -u fork sprint/2026-04-26/issue-489-rebased`.
- [ ] Open PR cross-fork: head `FrankFanteev:sprint/2026-04-26/issue-489-rebased`, base `peterdrier:main`. Title: `Add per-camp role assignments — rebased onto §15 main (closes nobodies-collective#489)`.
- [ ] In the PR body, link the prior PR #335 as the reviewed source of truth and call out that all design discussion already happened there.
- [ ] Close PR #335 once the new PR is open. Comment on #335 pointing at the new PR.

## Risks and gotchas

- **Notification API may differ.** `INotificationEmitter` on main may have a different parameter list than what I've sketched. Read it before coding the emit calls.
- **Repository pattern may not match my sketch.** Look at `CampRepository.AssignSlotAsync` peers to see how main writes mutating operations (some repos return the entity; some take a `Action<>` mutator; some accept the full entity to add). Match the local convention.
- **CampMember entity tweaks.** PR #228 may have added properties on main that our nav-only modification doesn't account for. Check `git diff origin/main 46bf7332 -- src/Humans.Domain/Entities/CampMember.cs` before editing.
- **`Directory.Packages.props` should NOT be carried.** Use main's package versions.
- **Plan for migration name collision** — main's `HumansDbContextModelSnapshot.cs` will change after our migration generates; that's expected.

## Reference material

- Source branch: `sprint/2026-04-24/issue-489` at `46bf7332`. View any file via `git show 46bf7332:<path>`.
- Closed PR (replaces): `https://github.com/peterdrier/Humans/pull/335`.
- Spec: `docs/superpowers/specs/2026-04-24-camp-roles-design.md`.
- Original implementation plan: `docs/superpowers/plans/2026-04-24-camp-roles-plan.md`.
- Reference mirror pattern: `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs` and `src/Humans.Application/Services/Camps/CampService.cs` on main — read both end-to-end before starting Phase E.
