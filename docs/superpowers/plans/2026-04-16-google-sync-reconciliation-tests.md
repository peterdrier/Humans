# Issue #508 — Google reconciliation soft-delete test rig

## Goal

Add end-to-end test coverage for `GoogleWorkspaceSyncService.SyncResourcesByTypeAsync` so
the six reconciliation scenarios from issue #508 are pinned by automated tests instead of
by careful reading + Codex review.

## Seam design

**Problem**: `GoogleWorkspaceSyncService` calls `CloudIdentityService.Groups.Memberships.*`
and `DriveService.Permissions.*` directly through the concrete Google SDK client classes.
Those classes are sealed / awkward to fake. Tests need a way to substitute the API surface
without touching the rest of the service.

**Approach**: Introduce two narrow internal interfaces — `IGoogleGroupMembershipClient`
and `IGoogleDrivePermissionClient` — that expose only the operations the reconciliation
path uses (list / create / delete). Real implementations wrap `CloudIdentityService` /
`DriveService`. Fake implementations back everything with dictionaries for tests.

The interfaces live in `src/Humans.Infrastructure/Google/` and are `internal` — exposed to
`Humans.Application.Tests` via the existing `InternalsVisibleTo` attribute.

`GoogleWorkspaceSyncService` gains a second `internal` constructor overload that takes the
two clients explicitly. The default production constructor builds them lazily from the
existing `GetDriveServiceAsync()` / `GetCloudIdentityServiceAsync()` helpers — so
production behavior is byte-identical, only reconciliation code paths route through the
clients.

**Out of scope**: `SyncTeamGroupMembersAsync`, group settings drift, path reconciliation,
and provisioning methods keep their direct Google SDK calls. Only the paths hit by
`SyncResourcesByTypeAsync` + `AddUserTo{Drive,Group}Async` + `RemoveUserFrom{Drive,Group}Async`
need the seam.

## Interface surface

```csharp
internal interface IGoogleGroupMembershipClient
{
    Task<IReadOnlyList<Membership>> ListMembershipsAsync(string groupId, CancellationToken ct);
    Task CreateMembershipAsync(string groupId, string userEmail, CancellationToken ct);
    Task DeleteMembershipAsync(string membershipName, CancellationToken ct);
}

internal interface IGoogleDrivePermissionClient
{
    Task<IReadOnlyList<Permission>> ListPermissionsAsync(string fileId, CancellationToken ct);
    Task<string> CreatePermissionAsync(string fileId, string userEmail, string role, CancellationToken ct);
    Task DeletePermissionAsync(string fileId, string permissionId, CancellationToken ct);
}
```

- Google SDK POCOs (`Membership`, `Permission`) are used as-is in return types — they're plain
  data classes the rest of the service already uses, so there's no translation layer.
- `CreatePermissionAsync` returns the new permission id so callers/tests can verify.
- `CreateMembershipAsync` does not return anything — the sync service doesn't use the result.
- Errors surface as thrown exceptions. 404/403 handling in `SyncGroupResourceAsync` keeps its
  `GoogleApiException` catch; fakes throw `GoogleApiException` or a simulated exception type
  when a test seeds an error scenario.

## Files to create

1. `src/Humans.Infrastructure/Google/IGoogleGroupMembershipClient.cs` — interface.
2. `src/Humans.Infrastructure/Google/IGoogleDrivePermissionClient.cs` — interface.
3. `src/Humans.Infrastructure/Google/RealGoogleGroupMembershipClient.cs` — wraps `CloudIdentityService`.
4. `src/Humans.Infrastructure/Google/RealGoogleDrivePermissionClient.cs` — wraps `DriveService`.
5. `tests/Humans.Application.Tests/Fakes/FakeGoogleGroupMembershipClient.cs` — in-memory impl.
6. `tests/Humans.Application.Tests/Fakes/FakeGoogleDrivePermissionClient.cs` — in-memory impl.
7. `tests/Humans.Application.Tests/Services/GoogleWorkspaceSyncServiceReconciliationTests.cs` — tests.

## Files to modify

1. `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs`:
   - Add `_groupMembershipClient` / `_drivePermissionClient` private fields.
   - Add private `GetGroupMembershipClientAsync()` / `GetDrivePermissionClientAsync()` that
     lazily build real clients when not overridden.
   - Add `internal` constructor overload accepting the two clients (for tests).
   - Refactor 6 call sites to go through the new clients:
     - `SyncGroupResourceAsync`: inline membership listing + `GoogleApiException` catch.
     - `SyncDriveResourceGroupAsync`: use helper that delegates to client.
     - `ExecuteDriveSyncActionsAsync`: the permission lookup before removal.
     - `AddUserToGroupAsync`: `.Memberships.Create(...)` call.
     - `RemoveUserFromGroupAsync`: lookup + `.Memberships.Delete(...)` call.
     - `AddUserToDriveAsync` / `RemoveUserFromDriveAsync`: `.Permissions.*` calls.
   - Keep `ListDrivePermissionsAsync` helper but make it delegate to the client (or remove).

## Fake design

### `FakeGoogleGroupMembershipClient`
- `Dictionary<string, List<Membership>> _memberships` keyed by `groupId`.
- `CreateMembershipAsync` adds a new `Membership` with `Name = "groups/{gid}/memberships/{guid}"`
  and `PreferredMemberKey.Id = email`. Throws 409 `GoogleApiException` if already present
  (so the idempotent path in `AddUserToGroupAsync` is exercised).
- `DeleteMembershipAsync` removes the matching `Name`.
- `ListMembershipsAsync` returns a snapshot of the list for the given group.
- `AddFailingGroup(groupId)` registers a group id whose list/create/delete operations all
  throw — used for the "shared-GoogleId error" scenario.
- `AddInitialMembership(groupId, email)` seeds an existing membership.

### `FakeGoogleDrivePermissionClient`
- `Dictionary<string, List<Permission>> _permissions` keyed by `fileId`.
- `CreatePermissionAsync` adds a new `Permission` with `Id = Guid`, `Type = "user"`,
  `EmailAddress = email`, `Role = role`, `PermissionDetails = null` (direct, not inherited).
- `DeletePermissionAsync` removes by `permissionId`.
- `AddInitialPermission(fileId, email, role, inherited: false)` seeds an existing permission.
  Setting `inherited: true` populates `PermissionDetails` with an inherited entry so the
  reconciler classifies it as `Inherited` (not `Extra`).
- `AddFailingFile(fileId)` — list/delete throws.

Both fakes also expose read-only snapshots (`GetMemberships(groupId)` / `GetPermissions(fileId)`)
so test assertions can read the post-execute state directly.

## Test scenarios (one `[Fact]` each)

All tests construct `GoogleWorkspaceSyncService` via the internal test constructor with the
fake clients, seed an InMemory `HumansDbContext` + `StubTeamResourceService`, and call
`SyncResourcesByTypeAsync(...SyncAction.Execute)` end-to-end.

### 1. `SoftDeletedTeam_RevokesDriveAndGroupPermissions_AndDeactivatesResourcesPerType`
- Seed: team (`IsActive = false`), 2 members (`LeftAt` set), 1 DriveFolder resource + 1 Group resource, both active.
- Fake Drive has direct permissions for both members on the folder.
- Fake Group has memberships for both members.
- Sync mode `AddAndRemove` for both services.
- Run `SyncResourcesByTypeAsync(DriveFolder)` → assert fake Drive has 0 permissions
  (SA allowed), DB row for DriveFolder is `IsActive = false`, DB row for Group is still
  `IsActive = true` (scoped per type).
- Run `SyncResourcesByTypeAsync(Group)` → assert fake Group has 0 memberships, DB row for
  Group is now `IsActive = false`.

### 2. `SharedGoogleIdDriveGroup_WhenApiFails_NoTeamInGroupIsDeactivated`
- Seed: two soft-deleted teams A and B, each with a DriveFolder row pointing at the same
  `GoogleId` (shared folder). Fake Drive is configured to throw on `ListPermissionsAsync`
  for that GoogleId.
- Run `SyncResourcesByTypeAsync(DriveFolder)` → assert both teams' DriveFolder rows remain
  `IsActive = true`, both rows retain `ErrorMessage`, and `DeactivateResourcesForTeamAsync`
  was not called (by asserting via a spy on `ITeamResourceService` or by reading DB state).

### 3. `PartialErrorWithinTeam_DefersDeactivationForTeamAndType`
- Seed: one soft-deleted team with TWO DriveFolder rows — different GoogleIds. Fake Drive
  throws on one of them, succeeds on the other.
- Run `SyncResourcesByTypeAsync(DriveFolder)` → assert BOTH rows remain `IsActive = true`
  (the clean row must not get swept up by a scoped deactivation call because the team has
  an errored row of the same type).

### 4. `DriveFolderPass_DoesNotDeactivateGroupRowForSoftDeletedTeam`
- Seed: soft-deleted team with DriveFolder + Group rows, fake Drive empty, fake Group with
  existing memberships.
- Run `SyncResourcesByTypeAsync(DriveFolder)` only.
- Assert: DriveFolder row `IsActive = false`, Group row still `IsActive = true` (would make
  the Group pass on the next tick skip the team otherwise).

### 5. `AddOnlyMode_DoesNotDeactivateSoftDeletedTeamResources`
- Same scenario as test 1, but sync mode set to `AddOnly` for GoogleDrive.
- Run `SyncResourcesByTypeAsync(DriveFolder)` → fake Drive permissions UNCHANGED (RemoveUser
  short-circuits on AddOnly), DB row STILL `IsActive = true` (post-execute cleanup skipped).

### 6. `NoneMode_DoesNotDeactivateSoftDeletedTeamResources`
- Same scenario as test 1, but sync mode set to `None` for GoogleDrive.
- Run `SyncResourcesByTypeAsync(DriveFolder)` → permissions UNCHANGED, DB row still active.

## Real `ITeamResourceService` vs stub in tests

The reconciliation service resolves `ITeamResourceService` via `IServiceProvider`. Tests can
wire the real `StubTeamResourceService` (which is a full impl, not a no-op — see
`TeamResourceServiceDeactivateTests`) or register `TeamResourceService` itself. Given the
production path is tested via `TeamResourceService` and the stub is used in other tests
already, prefer `StubTeamResourceService` unless it differs meaningfully from
`TeamResourceService.DeactivateResourcesForTeamAsync`.

Quick check: `StubTeamResourceService.DeactivateResourcesForTeamAsync` is "logically
identical" per `TeamResourceServiceDeactivateTests` docstring. Use it.

## Test base class

A `GoogleWorkspaceSyncServiceTestBase` helper encapsulates:
- InMemory `HumansDbContext` + `IDbContextFactory` (simple wrapper).
- `FakeClock`, null logger.
- Fake `IAuditLogService` (NSubstitute).
- `StubTeamResourceService` wired into an `IServiceProvider` substitute.
- `ISyncSettingsService` with mutable `SyncMode` state.
- `GoogleWorkspaceSettings` with a dummy service account JSON (so `GetServiceAccountEmailAsync`
  works and doesn't throw).
- `FakeGoogleDrivePermissionClient` + `FakeGoogleGroupMembershipClient`.
- Seeding helpers: `SeedTeam(softDeleted: bool)`, `SeedMember(teamId, email, leftAt: Instant?)`,
  `SeedGoogleResource(teamId, type, googleId, isActive)`.

## Execution order

1. Refactor `GoogleWorkspaceSyncService` to introduce the clients (interfaces + real impls +
   field + getter + internal constructor + refactor call sites). Build to confirm unchanged
   behavior.
2. Run full `dotnet test Humans.slnx` to confirm no regression.
3. Add fakes.
4. Add test base class + tests one at a time. Each test drives any fake API clarifications
   needed.
5. Final build + test + `dotnet format`.

## Verification checklist

- [ ] `dotnet build Humans.slnx` clean.
- [ ] `dotnet test Humans.slnx --filter FullyQualifiedName~GoogleWorkspaceSyncServiceReconciliationTests` → 6 new tests pass.
- [ ] `dotnet test Humans.slnx` → no regressions in the existing `Humans.Application.Tests` suite.
- [ ] All 6 acceptance criteria from issue #508 have a corresponding named `[Fact]`.
- [ ] Fakes live in `tests/Humans.Application.Tests/Fakes/` and are reusable (no per-test state leakage).
- [ ] No production API change visible to non-test callers (`internal` ctor only).

## PR

- Base: `peterdrier/Humans:main` (PR #227 is here but not upstream yet).
- Title: `Add end-to-end reconciliation test rig for Google sync soft-delete paths (#508)`.
- Body: scenario list + note that seam is `internal`-only.
- Closes: #508.
