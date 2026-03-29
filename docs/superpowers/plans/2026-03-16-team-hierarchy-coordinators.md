# Team Hierarchy and Leads→Coordinators Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename Leads→Coordinators, replace hardcoded "Lead" name matching with `IsManagement` flag, and add one-level team hierarchy (departments).

**Architecture:** Three layered changes applied bottom-up: domain enums/entities first, then infrastructure (EF config, services, jobs), then web layer (controllers, views, localization). Single EF migration covers all schema changes. App-level constraints for IsManagement uniqueness and hierarchy depth.

**Tech Stack:** .NET 9, EF Core 9, PostgreSQL, NodaTime, ASP.NET MVC

**Spec:** `docs/superpowers/specs/2026-03-16-team-hierarchy-coordinators-design.md`

---

## Chunk 1: Domain Layer Changes

### Task 1: Rename enums and constants

**Files:**
- Modify: `src/Humans.Domain/Enums/TeamMemberRole.cs`
- Modify: `src/Humans.Domain/Enums/SystemTeamType.cs`
- Modify: `src/Humans.Domain/Enums/ContactFieldVisibility.cs`
- Modify: `src/Humans.Domain/Constants/SystemTeamIds.cs`

- [ ] **Step 1: Rename `TeamMemberRole.Lead` → `Coordinator`**

In `src/Humans.Domain/Enums/TeamMemberRole.cs`, rename the enum member. The integer value `1` stays the same.

```csharp
Coordinator = 1
```

Update the doc comment from "Team lead" to "Team coordinator".

- [ ] **Step 2: Rename `SystemTeamType.Leads` → `Coordinators`**

In `src/Humans.Domain/Enums/SystemTeamType.cs`, rename the enum member. Value `2` stays the same.

```csharp
Coordinators = 2
```

Update the doc comment from "leads" to "coordinators".

- [ ] **Step 3: Rename `ContactFieldVisibility.LeadsAndBoard` → `CoordinatorsAndBoard`**

In `src/Humans.Domain/Enums/ContactFieldVisibility.cs`, rename the enum member. Value `1` stays the same.

```csharp
CoordinatorsAndBoard = 1
```

Update the doc comment.

- [ ] **Step 4: Rename `SystemTeamIds.Leads` → `Coordinators`**

In `src/Humans.Domain/Constants/SystemTeamIds.cs`, rename the constant:

```csharp
public static readonly Guid Coordinators = Guid.Parse("00000000-0000-0000-0001-000000000002");
```

- [ ] **Step 5: Do NOT commit yet**

The domain renames break compilation across the entire solution. Continue with Tasks 2-3 (also domain layer), then commit all domain changes together at the end of Chunk 1 once the entities are also updated.

### Task 2: Add `IsManagement` to `TeamRoleDefinition`, remove `IsLeadRole`

**Files:**
- Modify: `src/Humans.Domain/Entities/TeamRoleDefinition.cs`

- [ ] **Step 1: Replace `IsLeadRole` with `IsManagement`**

In `src/Humans.Domain/Entities/TeamRoleDefinition.cs`:

Remove the computed property at line 64:
```csharp
public bool IsLeadRole => string.Equals(Name, "Lead", StringComparison.OrdinalIgnoreCase);
```

Add a persisted property in its place:
```csharp
/// <summary>
/// Whether this role is the team's management/coordination role.
/// At most one role per team can have this set to true.
/// Assigning a member to this role automatically sets their TeamMemberRole to Coordinator.
/// </summary>
public bool IsManagement { get; set; }
```

- [ ] **Step 2: Continue to Task 3 (no commit yet)**

### Task 3: Add `ParentTeamId` to `Team`

**Files:**
- Modify: `src/Humans.Domain/Entities/Team.cs`

- [ ] **Step 1: Add hierarchy properties**

In `src/Humans.Domain/Entities/Team.cs`, add after the `UpdatedAt` property (line 66):

```csharp
/// <summary>
/// Optional parent team ID for one-level hierarchy (departments).
/// A team with a parent cannot itself be a parent.
/// </summary>
public Guid? ParentTeamId { get; set; }

/// <summary>
/// Navigation property to the parent team (department).
/// </summary>
public Team? ParentTeam { get; set; }

/// <summary>
/// Navigation property to child teams (sub-teams of this department).
/// </summary>
public ICollection<Team> ChildTeams { get; } = new List<Team>();
```

- [ ] **Step 2: Update the `RequiresApproval` doc comment**

Change line 37 from "approval from a lead" to "approval from a coordinator":

```csharp
/// Whether joining this team requires approval from a coordinator or board member.
```

- [ ] **Step 3: Commit all domain changes together**

```bash
git add src/Humans.Domain/
git commit -m "refactor: rename Lead→Coordinator, add IsManagement and ParentTeamId (#130)"
```

---

## Chunk 2: EF Configuration and Migration

### Task 4: Update EF configurations

**Files:**
- Modify: `src/Humans.Infrastructure/Data/Configurations/TeamRoleDefinitionConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/TeamConfiguration.cs`

- [ ] **Step 1: Update TeamRoleDefinitionConfiguration**

In `TeamRoleDefinitionConfiguration.cs`:

1. Remove `builder.Ignore(d => d.IsLeadRole);` (line 60) — this property no longer exists.
2. Add configuration for `IsManagement`:

```csharp
builder.Property(d => d.IsManagement)
    .IsRequired()
    .HasDefaultValue(false);
```

- [ ] **Step 2: Update TeamConfiguration seed data**

In `TeamConfiguration.cs`, update the Leads system team seed (lines 94-106):

- Change `Name = "Leads"` → `Name = "Coordinators"`
- Change `Description = "All team leads"` → `Description = "All team coordinators"`
- Change `Slug = "leads"` → `Slug = "coordinators"`
- Change `SystemTeamType = SystemTeamType.Leads` → `SystemTeamType = SystemTeamType.Coordinators`

Also update any other references to `SystemTeamType.Leads` in seed data.

- [ ] **Step 3: Add ParentTeamId configuration**

In `TeamConfiguration.cs`, add after existing property configurations:

```csharp
builder.Property(t => t.ParentTeamId);

builder.HasOne(t => t.ParentTeam)
    .WithMany(t => t.ChildTeams)
    .HasForeignKey(t => t.ParentTeamId)
    .OnDelete(DeleteBehavior.Restrict);
```

Add `ParentTeamId = (Guid?)null` to ALL FIVE system team seed data objects (Volunteers, Coordinators, Board, Asociados, Colaboradors). EF Core requires all properties in anonymous seed objects.

- [ ] **Step 4: Build to verify configuration compiles**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj`

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/TeamRoleDefinitionConfiguration.cs src/Humans.Infrastructure/Data/Configurations/TeamConfiguration.cs
git commit -m "refactor: update EF configurations for IsManagement, ParentTeamId, and Coordinators rename (#130)"
```

### Task 5: Create EF migration

**Files:**
- Create: `src/Humans.Infrastructure/Data/Migrations/<timestamp>_TeamHierarchyAndCoordinators.cs`

- [ ] **Step 1: Generate the migration**

```bash
dotnet ef migrations add TeamHierarchyAndCoordinators --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

- [ ] **Step 2: Edit migration to add data updates**

Open the generated migration file. In the `Up` method, add these SQL statements **before** the schema changes:

```csharp
// Rename string-stored enum values
migrationBuilder.Sql("UPDATE team_members SET \"Role\" = 'Coordinator' WHERE \"Role\" = 'Lead'");
migrationBuilder.Sql("UPDATE teams SET \"SystemTeamType\" = 'Coordinators' WHERE \"SystemTeamType\" = 'Leads'");
migrationBuilder.Sql("UPDATE contact_fields SET \"Visibility\" = 'CoordinatorsAndBoard' WHERE \"Visibility\" = 'LeadsAndBoard'");
migrationBuilder.Sql("UPDATE user_emails SET \"Visibility\" = 'CoordinatorsAndBoard' WHERE \"Visibility\" = 'LeadsAndBoard'");

// Rename system team
migrationBuilder.Sql(@"
    UPDATE teams
    SET ""Name"" = 'Coordinators', ""Slug"" = 'coordinators', ""Description"" = 'All team coordinators'
    WHERE ""Id"" = '00000000-0000-0000-0001-000000000002'");

// Set IsManagement on existing Lead role definitions and rename them
migrationBuilder.Sql(@"
    UPDATE team_role_definitions
    SET ""IsManagement"" = true, ""Name"" = 'Coordinator'
    WHERE ""Name"" = 'Lead'");
```

In the `Down` method, add the reverse operations.

- [ ] **Step 3: Verify migration compiles**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj`

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Data/Migrations/
git commit -m "feat: add EF migration for team hierarchy and Coordinators rename (#130)"
```

---

## Chunk 3: Service Layer Changes

### Task 6: Update ITeamService interface and TeamService implementation

**Files:**
- Modify: `src/Humans.Application/Interfaces/ITeamService.cs`
- Modify: `src/Humans.Infrastructure/Services/TeamService.cs`

**ITeamService changes (do these first so the interface matches the implementation):**

- [ ] **Step 0a: Update CachedTeam record (ITeamService.cs lines 7-10)**

```csharp
public record CachedTeam(
    Guid Id, string Name, string? Description, string Slug,
    bool IsSystemTeam, SystemTeamType SystemTeamType, bool RequiresApproval,
    Instant CreatedAt, List<CachedTeamMember> Members,
    Guid? ParentTeamId = null, List<Guid>? ChildTeamIds = null);
```

- [ ] **Step 0b: Rename `IsUserLeadOfTeamAsync` → `IsUserCoordinatorOfTeamAsync` on ITeamService (lines 162-168)**

- [ ] **Step 0c: Remove `SetMemberRoleAsync` from ITeamService (lines 185-193)**

- [ ] **Step 0d: Add `SetRoleIsManagementAsync` to ITeamService**

```csharp
/// <summary>
/// Sets or clears the IsManagement flag on a role definition.
/// Cannot be changed while members are assigned to the role.
/// </summary>
Task SetRoleIsManagementAsync(
    Guid roleDefinitionId, bool isManagement, Guid actorUserId,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 0e: Update `CreateTeamAsync` signature to accept `parentTeamId`**

- [ ] **Step 0f: Update `UpdateTeamAsync` signature to accept `parentTeamId`**

- [ ] **Step 0g: Update `LeaveTeamAsync` return type to `Task<bool>` (returns true if leaving member was a Coordinator)**

- [ ] **Step 0h: Update `RemoveMemberAsync` return type to `Task<bool>` (returns true if removed member was a Coordinator)**

**TeamService changes:**

- [ ] **Step 1: Update `CreateTeamAsync` (line 80-93)**

Change the auto-created role from "Lead" to "Coordinator" with `IsManagement = true`:

```csharp
// Auto-create Coordinator role definition for non-system teams
var coordinatorRole = new TeamRoleDefinition
{
    Id = Guid.NewGuid(),
    TeamId = team.Id,
    Name = "Coordinator",
    Description = "Team coordination role",
    SlotCount = 1,
    IsManagement = true,
    Priorities = [SlotPriority.Critical],
    SortOrder = 0,
    CreatedAt = now,
    UpdatedAt = now
};
_dbContext.Set<TeamRoleDefinition>().Add(coordinatorRole);
```

- [ ] **Step 2: Update `reservedSlugs` (line 55)**

Add `"departments"` to the reserved slugs array.

- [ ] **Step 3: Update `CreateRoleDefinitionAsync` (line 958)**

Remove the "Lead" name reservation check:

```csharp
// REMOVE these lines (958-961):
// if (string.Equals(name, "Lead", StringComparison.OrdinalIgnoreCase))
// {
//     throw new InvalidOperationException("The 'Lead' role name is reserved...");
// }
```

- [ ] **Step 4: Update `UpdateRoleDefinitionAsync` (lines 1041-1053)**

Replace the "cannot rename to/from Lead" checks with `IsManagement` toggle protection:

```csharp
// Cannot toggle IsManagement on a role with assigned members
// (IsManagement changes are handled separately, not through rename)
```

Remove the entire `if` block at lines 1041-1053 that prevents renaming to/from "Lead". Names are no longer significant.

- [ ] **Step 5: Update `DeleteRoleDefinitionAsync` (lines 1110-1113)**

Replace the `IsLeadRole` check with `IsManagement` + assignments check:

```csharp
if (definition.IsManagement && definition.Assignments.Count > 0)
{
    throw new InvalidOperationException("Cannot delete the management role while members are assigned to it. Unassign all members first.");
}
```

Note: management roles CAN be deleted if no one is assigned (unlike the old "Lead" which could never be deleted).

- [ ] **Step 6: Update `AssignToRoleAsync` (lines 1252-1257)**

Replace `definition.IsLeadRole` with `definition.IsManagement`:

```csharp
// If this is a management role, set TeamMember.Role = Coordinator
if (definition.IsManagement && teamMember.Role != TeamMemberRole.Coordinator)
{
    teamMember.Role = TeamMemberRole.Coordinator;
}
```

- [ ] **Step 7: Update `UnassignFromRoleAsync` (lines 1324-1337)**

Replace `definition.IsLeadRole` with `definition.IsManagement`, and replace the `Name == "Lead"` EF query with `IsManagement`:

```csharp
// If this is a management role, check if member has remaining management assignments
if (definition.IsManagement)
{
    var member = assignment.TeamMember;
    var hasOtherManagementAssignments = await _dbContext.Set<TeamRoleAssignment>()
        .AnyAsync(a => a.TeamMemberId == teamMemberId
            && a.Id != assignment.Id
            && a.TeamRoleDefinition.IsManagement, cancellationToken);

    if (!hasOtherManagementAssignments && member.Role == TeamMemberRole.Coordinator)
    {
        member.Role = TeamMemberRole.Member;
    }
}
```

Also update the cache section (lines 1341-1345) to check `definition.IsManagement` instead of `definition.IsLeadRole`.

- [ ] **Step 8: Update `IsUserLeadOfTeamAsync` → `IsUserCoordinatorOfTeamAsync` (lines 654-663)**

Rename the method and update the query to use `TeamMemberRole.Coordinator`:

```csharp
public async Task<bool> IsUserCoordinatorOfTeamAsync(
    Guid teamId, Guid userId, CancellationToken cancellationToken = default)
{
    return await _dbContext.TeamMembers
        .AnyAsync(tm => tm.TeamId == teamId && tm.UserId == userId
            && tm.Role == TeamMemberRole.Coordinator && tm.LeftAt == null,
            cancellationToken);
}
```

- [ ] **Step 9: Remove `SetMemberRoleAsync` (lines 700-774)**

Delete the entire method. Promotion/demotion is now implicit via role assignment.

- [ ] **Step 10: Update all remaining `TeamMemberRole.Lead` references**

Search the file for any remaining `.Lead` references and change to `.Coordinator`. Key methods to check:
- `CanUserApproveRequestsForTeamAsync`
- `GetPendingRequestsForApproverAsync` (line 565: `tm.Role == TeamMemberRole.Lead`)
- `BuildCachedTeam` — update to pass `ParentTeamId` and `ChildTeamIds` to the new `CachedTeam` constructor
- The inline `CachedTeam` constructor in `CreateTeamAsync` (around line 101) — also needs the new parameters

- [ ] **Step 11: Update all remaining `SystemTeamType.Leads` / `SystemTeamIds.Leads` references**

Change to `.Coordinators` throughout the file.

- [ ] **Step 12: Add hierarchy validation to team operations**

In `CreateTeamAsync`, add a `parentTeamId` parameter and validation:

Update the method signature in both `ITeamService` and `TeamService`:

```csharp
Task<Team> CreateTeamAsync(
    string name, string? description, bool requiresApproval,
    Guid? parentTeamId = null, string? googleGroupPrefix = null,
    CancellationToken cancellationToken = default);
```

Add validation before creating the team:

```csharp
if (parentTeamId.HasValue)
{
    var parent = await _dbContext.Teams
        .Include(t => t.ChildTeams)
        .FirstOrDefaultAsync(t => t.Id == parentTeamId.Value, cancellationToken)
        ?? throw new InvalidOperationException($"Parent team {parentTeamId.Value} not found");

    if (parent.IsSystemTeam)
        throw new InvalidOperationException("System teams cannot be parents");

    if (parent.ParentTeamId.HasValue)
        throw new InvalidOperationException("Cannot nest more than one level — the parent team already has a parent");
}
```

Set `ParentTeamId = parentTeamId` on the new team entity.

- [ ] **Step 13: Add hierarchy validation to `UpdateTeamAsync`**

Add a `parentTeamId` parameter to `UpdateTeamAsync` and validate:

```csharp
Task<Team> UpdateTeamAsync(
    Guid teamId, string name, string? description, bool requiresApproval, bool isActive,
    Guid? parentTeamId = null, string? googleGroupPrefix = null,
    CancellationToken cancellationToken = default);
```

Validation:
```csharp
if (parentTeamId.HasValue)
{
    if (parentTeamId.Value == teamId)
        throw new InvalidOperationException("A team cannot be its own parent");

    if (team.IsSystemTeam)
        throw new InvalidOperationException("System teams cannot have parents");

    // Check if this team has children (would create >1 level)
    var hasChildren = await _dbContext.Teams.AnyAsync(t => t.ParentTeamId == teamId && t.IsActive, cancellationToken);
    if (hasChildren)
        throw new InvalidOperationException("This team has sub-teams and cannot become a child of another team");

    var parent = await _dbContext.Teams.FindAsync(new object[] { parentTeamId.Value }, cancellationToken)
        ?? throw new InvalidOperationException($"Parent team {parentTeamId.Value} not found");

    if (parent.IsSystemTeam)
        throw new InvalidOperationException("System teams cannot be parents");

    if (parent.ParentTeamId.HasValue)
        throw new InvalidOperationException("Cannot nest more than one level — the parent team already has a parent");
}

team.ParentTeamId = parentTeamId;
```

Also update the cache `with` expression in `UpdateTeamAsync` (around line 206) to include `ParentTeamId`:
```csharp
cached[teamId] = existing with { Name = name, Description = description, RequiresApproval = requiresApproval, ParentTeamId = parentTeamId };
```

- [ ] **Step 14: Add child check to `DeleteTeamAsync` (line 220)**

After the system team check (line 228), add:

```csharp
var hasActiveChildren = await _dbContext.Teams.AnyAsync(t => t.ParentTeamId == teamId && t.IsActive, cancellationToken);
if (hasActiveChildren)
{
    throw new InvalidOperationException("Cannot deactivate a team that has active sub-teams. Remove or reassign sub-teams first.");
}
```

- [ ] **Step 15: Add `IsManagement` toggle protection**

In `UpdateRoleDefinitionAsync`, add a new service method or in-method validation to prevent changing `IsManagement` when the role has assigned members. Since `UpdateRoleDefinitionAsync` doesn't currently accept an `IsManagement` parameter, add a separate method:

```csharp
public async Task SetRoleIsManagementAsync(
    Guid roleDefinitionId, bool isManagement, Guid actorUserId,
    CancellationToken cancellationToken = default)
```

This method:
1. Loads the role definition with assignments
2. If the role has assigned members, throws: "Cannot change IsManagement while members are assigned"
3. If setting to true, checks no other role in the same team already has `IsManagement = true`
4. Sets the flag and saves

- [ ] **Step 16: Add coordinator sync to leave/remove paths**

Change `LeaveTeamAsync` and `RemoveMemberAsync` to return `Task<bool>` — returning `true` if the departing member had `TeamMemberRole.Coordinator`. Check the member's role BEFORE setting `LeftAt`:

```csharp
var wasCoordinator = member.Role == TeamMemberRole.Coordinator;
member.LeftAt = _clock.GetCurrentInstant();
// ... save, cache update ...
return wasCoordinator;
```

The controller layer will check this return value and call `SyncCoordinatorsMembershipForUserAsync` if true.

**Important:** `TeamController` does NOT currently inject `ISystemTeamSync`. Add `ISystemTeamSync` to `TeamController`'s constructor for the `Leave` action to trigger sync. `TeamAdminController` already has it for `RemoveMember`.

- [ ] **Step 17: Build to check progress**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj 2>&1 | head -50`

Fix any remaining compilation errors.

- [ ] **Step 18: Commit**

```bash
git add src/Humans.Infrastructure/Services/TeamService.cs src/Humans.Application/Interfaces/ITeamService.cs
git commit -m "refactor: replace Lead string checks with IsManagement, add hierarchy validation (#130)"
```

### Task 7: Update SystemTeamSyncJob

**Files:**
- Modify: `src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs`
- Modify: `src/Humans.Application/Interfaces/ISystemTeamSync.cs`

- [ ] **Step 1: Rename methods on ISystemTeamSync**

In `ISystemTeamSync.cs`, rename:
- `SyncLeadsMembershipForUserAsync` → `SyncCoordinatorsMembershipForUserAsync`

- [ ] **Step 2: Rename methods in SystemTeamSyncJob**

Rename throughout the file:
- `SyncLeadsTeamAsync` → `SyncCoordinatorsTeamAsync`
- `SyncLeadsMembershipForUserAsync` → `SyncCoordinatorsMembershipForUserAsync`

- [ ] **Step 3: Update enum references**

Change all `TeamMemberRole.Lead` → `.Coordinator` and `SystemTeamType.Leads` → `.Coordinators` in the file.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs src/Humans.Application/Interfaces/ISystemTeamSync.cs
git commit -m "refactor: rename SyncLeads→SyncCoordinators in SystemTeamSyncJob (#130)"
```

### Task 8: Update remaining service files

**Files:**
- Modify: `src/Humans.Infrastructure/Services/ContactFieldService.cs`
- Modify: `src/Humans.Infrastructure/Services/ConsentService.cs`
- Modify: `src/Humans.Infrastructure/Services/StubTeamResourceService.cs`
- Modify: `src/Humans.Infrastructure/Services/TeamResourceService.cs`
- Modify: `src/Humans.Infrastructure/Jobs/SendBoardDailyDigestJob.cs`
- Modify: `src/Humans.Infrastructure/Services/MembershipCalculator.cs`

- [ ] **Step 1: Update ContactFieldService**

Rename `_cachedIsAnyLead` → `_cachedIsAnyCoordinator` (lines 22, 179, 186).
Change `TeamMemberRole.Lead` → `.Coordinator`.
Change `ContactFieldVisibility.LeadsAndBoard` → `.CoordinatorsAndBoard`.

- [ ] **Step 2: Update ConsentService**

Change `SyncLeadsMembershipForUserAsync` → `SyncCoordinatorsMembershipForUserAsync` (line 154).

- [ ] **Step 3: Update StubTeamResourceService and TeamResourceService**

Change `IsUserLeadOfTeamAsync` → `IsUserCoordinatorOfTeamAsync`.

- [ ] **Step 4: Update SendBoardDailyDigestJob**

Change `TeamMemberRole.Lead` → `.Coordinator` (line 120).

- [ ] **Step 5: Update MembershipCalculator**

Change `SystemTeamIds.Leads` → `.Coordinators` and `TeamMemberRole.Lead` → `.Coordinator`.

- [ ] **Step 6: Build Infrastructure project**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj`

Fix any remaining errors.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Infrastructure/
git commit -m "refactor: update remaining services for Coordinator rename (#130)"
```

---

## Chunk 4: Web Layer Changes

### Task 9: Update controllers

**Files:**
- Modify: `src/Humans.Web/Controllers/TeamController.cs`
- Modify: `src/Humans.Web/Controllers/TeamAdminController.cs`

- [ ] **Step 1: Update TeamController**

1. **Inject `ISystemTeamSync`** into the constructor (needed for coordinator sync on leave)
2. Rename all `IsCurrentUserLead` → `IsCurrentUserCoordinator` (lines 73, 189)
3. Rename all `IsLead` → `IsCoordinator` (lines 186, 392)
4. Change `TeamMemberRole.Lead` → `.Coordinator`
5. Change `IsUserLeadOfTeamAsync` → `IsUserCoordinatorOfTeamAsync` (line 117)
6. In the `Leave` action: check `LeaveTeamAsync` return value; if `true`, call `SyncCoordinatorsMembershipForUserAsync`
7. Update `CreateTeam`/`EditTeam` actions to pass `parentTeamId`
8. Add `Departments` action:

```csharp
[HttpGet("departments")]
public async Task<IActionResult> Departments(CancellationToken cancellationToken)
{
    var teams = await _teamService.GetAllTeamsAsync(cancellationToken);
    // Build tree: departments (teams with children), standalone teams, child teams
    var activeTeams = teams.Where(t => t.IsActive && !t.IsSystemTeam).ToList();
    var departments = activeTeams.Where(t => t.ChildTeams.Any(c => c.IsActive)).ToList();
    var standalone = activeTeams.Where(t => t.ParentTeamId == null && !t.ChildTeams.Any(c => c.IsActive)).ToList();

    var model = new DepartmentsViewModel
    {
        Departments = departments,
        StandaloneTeams = standalone
    };
    return View(model);
}
```

- [ ] **Step 2: Update TeamAdminController**

1. Remove `SetRole` action (the `POST Members/{userId}/SetRole` endpoint) and its sync call
2. Change all `SyncLeadsMembershipForUserAsync` → `SyncCoordinatorsMembershipForUserAsync` (lines 768, 812)
3. Change `IsLead` → `IsCoordinator` (lines 169, 617)
4. Change `TeamMemberRole.Lead` → `.Coordinator`
5. In `RemoveMember` action: check `RemoveMemberAsync` return value; if `true`, call `SyncCoordinatorsMembershipForUserAsync`

- [ ] **Step 3: Build to check**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj 2>&1 | head -50`

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/
git commit -m "refactor: update controllers for Coordinator rename, remove SetRole, add Departments (#130)"
```

### Task 10: Update view models

**Files:**
- Modify: `src/Humans.Web/Models/TeamViewModels.cs`
- Modify: `src/Humans.Web/Models/ProfileViewModel.cs`
- Create: `src/Humans.Web/Models/DepartmentsViewModel.cs` (if needed, or add to TeamViewModels.cs)

- [ ] **Step 1: Rename `IsLead` → `IsCoordinator` in view models**

In `TeamViewModels.cs`:
- `TeamMemberViewModel.IsLead` → `IsCoordinator` (line 64)
- `MyTeamMembershipViewModel.IsLead` → `IsCoordinator` (line 87)
- `TeamSummaryViewModel.IsCurrentUserLead` → `IsCurrentUserCoordinator` (line 24)
- `TeamDetailViewModel.IsCurrentUserLead` → `IsCurrentUserCoordinator` (line 45)
- `TeamRoleDefinitionViewModel.IsLeadRole` → `IsManagement` (line 255), and update its `FromEntity` mapping (line 300) from `d.IsLeadRole` → `d.IsManagement`

In `ProfileViewModel.cs`:
- `TeamMembershipViewModel.IsLead` → `IsCoordinator` (line 318)

- [ ] **Step 2: Add DepartmentsViewModel**

Add to `TeamViewModels.cs`:

```csharp
public class DepartmentsViewModel
{
    public required List<Team> Departments { get; init; }
    public required List<Team> StandaloneTeams { get; init; }
}
```

- [ ] **Step 3: Add parent/child info to relevant view models**

Add to `TeamDetailViewModel`:
```csharp
public Team? ParentTeam { get; init; }
public IReadOnlyList<Team> ChildTeams { get; init; } = [];
```

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Models/
git commit -m "refactor: update view models for Coordinator rename and departments (#130)"
```

### Task 11: Update views

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_RoleBadge.cshtml`
- Modify: `src/Humans.Web/Views/Team/Details.cshtml`
- Modify: `src/Humans.Web/Views/Team/MyTeams.cshtml`
- Modify: `src/Humans.Web/Views/Team/_TeamCard.cshtml`
- Modify: `src/Humans.Web/Views/TeamAdmin/Members.cshtml`
- Modify: `src/Humans.Web/Views/TeamAdmin/Roles.cshtml`
- Modify: `src/Humans.Web/Views/Shared/Components/ProfileCard/Default.cshtml`
- Modify: `src/Humans.Web/Views/Profile/Edit.cshtml`
- Modify: `src/Humans.Web/Views/Profile/Emails.cshtml`
- Modify: `src/Humans.Web/Views/Team/CreateTeam.cshtml`
- Modify: `src/Humans.Web/Views/Team/EditTeam.cshtml`
- Create: `src/Humans.Web/Views/Team/Departments.cshtml`

- [ ] **Step 1: Update `_RoleBadge.cshtml`**

Change `TeamMemberRole.Lead` → `.Coordinator` and `Profile_Lead` localizer key → `Profile_Coordinator`.

- [ ] **Step 2: Update `Details.cshtml`**

1. Change `IsLead` → `IsCoordinator` (lines 77-80)
2. Change `IsCurrentUserLead` → `IsCurrentUserCoordinator` (line 30)
3. Add parent breadcrumb and child teams section:

```html
@if (Model.ParentTeam != null)
{
    <nav aria-label="breadcrumb">
        <ol class="breadcrumb">
            <li class="breadcrumb-item"><a asp-action="Details" asp-route-slug="@Model.ParentTeam.Slug">@Model.ParentTeam.Name</a></li>
            <li class="breadcrumb-item active">@Model.Name</li>
        </ol>
    </nav>
}

@if (Model.ChildTeams.Any())
{
    <h4>Sub-teams</h4>
    <ul>
        @foreach (var child in Model.ChildTeams)
        {
            <li><a asp-action="Details" asp-route-slug="@child.Slug">@child.Name</a></li>
        }
    </ul>
}
```

- [ ] **Step 3: Update remaining views**

For each view, change:
- `IsLead` → `IsCoordinator`
- `IsCurrentUserLead` → `IsCurrentUserCoordinator`
- `IsLeadRole` → `IsManagement`
- `TeamMemberRole.Lead` → `.Coordinator`
- `"LeadsAndBoard"` → `"CoordinatorsAndBoard"` (in Profile/Edit.cshtml and Profile/Emails.cshtml)

In `TeamAdmin/Members.cshtml`: remove the Promote/Demote UI actions that used `SetRole`.
In `TeamAdmin/Roles.cshtml`: replace `IsLeadRole` checks with `IsManagement`.

- [ ] **Step 4: Update Create/Edit Team forms**

Add optional parent team dropdown to `CreateTeam.cshtml` and `EditTeam.cshtml`:

```html
<div class="mb-3">
    <label asp-for="ParentTeamId" class="form-label">Department (optional)</label>
    <select asp-for="ParentTeamId" asp-items="Model.EligibleParents" class="form-select">
        <option value="">None (standalone team)</option>
    </select>
</div>
```

- [ ] **Step 5: Create `Departments.cshtml`**

Create a new view at `src/Humans.Web/Views/Team/Departments.cshtml` showing the tree layout:

```html
@model Humans.Web.Models.DepartmentsViewModel
@{ ViewData["Title"] = "Departments"; }

<h1>Departments</h1>

@foreach (var dept in Model.Departments.OrderBy(d => d.Name))
{
    <div class="card mb-3">
        <div class="card-header">
            <h5 class="mb-0">
                <a asp-action="Details" asp-route-slug="@dept.Slug">@dept.Name</a>
            </h5>
            @if (!string.IsNullOrEmpty(dept.Description))
            {
                <small class="text-muted">@dept.Description</small>
            }
        </div>
        <ul class="list-group list-group-flush">
            @foreach (var child in dept.ChildTeams.Where(c => c.IsActive).OrderBy(c => c.Name))
            {
                <li class="list-group-item">
                    <a asp-action="Details" asp-route-slug="@child.Slug">@child.Name</a>
                    @if (!string.IsNullOrEmpty(child.Description))
                    {
                        <small class="text-muted d-block">@child.Description</small>
                    }
                </li>
            }
        </ul>
    </div>
}

@if (Model.StandaloneTeams.Any())
{
    <h4 class="mt-4">Other Teams</h4>
    <div class="list-group">
        @foreach (var team in Model.StandaloneTeams.OrderBy(t => t.Name))
        {
            <a asp-action="Details" asp-route-slug="@team.Slug" class="list-group-item list-group-item-action">
                <strong>@team.Name</strong>
                @if (!string.IsNullOrEmpty(team.Description))
                {
                    <small class="text-muted d-block">@team.Description</small>
                }
            </a>
        }
    </div>
}
```

- [ ] **Step 6: Add Departments link to navigation**

Add a "Departments" link to the Teams section of the nav bar (in `_Layout.cshtml` or the relevant nav partial) so the page is discoverable.

- [ ] **Step 7: Update MyTeams.cshtml**

Group teams under parent departments. Change `IsLead` → `IsCoordinator`.

- [ ] **Step 8: Build to verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`

- [ ] **Step 9: Commit**

```bash
git add src/Humans.Web/Views/
git commit -m "feat: update views for Coordinator rename, add Departments page (#130)"
```

### Task 12: Update localization

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.es.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.de.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.fr.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.it.resx`

- [ ] **Step 1: Update English resource file**

In `SharedResource.resx`:
- Rename key `Profile_Lead` → `Profile_Coordinator`, value "Team Lead" → "Coordinator"
- Rename key `TeamDetail_TeamLeads` → `TeamDetail_TeamCoordinators`, value "Team Leads" → "Coordinators"
- Remove keys `TeamAdmin_PromoteToLead` and `TeamAdmin_PromoteConfirm` (UI actions removed)
- Remove key `TeamAdmin_DemoteConfirm` and `TeamAdmin_DemoteToMember` (UI actions removed)
- Update any remaining "lead" text to "coordinator"

- [ ] **Step 2: Update Spanish (es) resource file**

Same key renames. Value for `Profile_Coordinator` = "Coordinator" (kept in English per spec).

- [ ] **Step 3: Update German (de) resource file**

Same key renames. Value for `Profile_Coordinator` = "Coordinator" (kept in English).

- [ ] **Step 4: Update French (fr) resource file**

Same key renames. Value for `Profile_Coordinator` = "Coordinator" (kept in English).

- [ ] **Step 5: Update Italian (it) resource file**

Same key renames. Value for `Profile_Coordinator` = "Coordinator" (kept in English).

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Resources/
git commit -m "refactor: update localization for Coordinator rename across 5 locales (#130)"
```

---

## Chunk 5: Tests and Documentation

### Task 13: Update tests

**Files:**
- Modify: `tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs`
- Modify: `tests/Humans.Application.Tests/Services/TeamServiceTests.cs`
- Modify: `tests/Humans.Application.Tests/Services/TeamRoleServiceTests.cs`
- Modify: `tests/Humans.Application.Tests/Services/MembershipCalculatorTests.cs`
- Modify: `tests/Humans.Application.Tests/Services/ContactFieldServiceTests.cs`
- Modify: `tests/Humans.Application.Tests/Services/ConsentServiceTests.cs`

- [ ] **Step 1: Update EnumStringStabilityTests**

Change expected string values:
- `TeamMemberRole.Lead` test → expect "Coordinator"
- `SystemTeamType.Leads` test → expect "Coordinators"
- `ContactFieldVisibility.LeadsAndBoard` test → expect "CoordinatorsAndBoard"

- [ ] **Step 2: Update TeamServiceTests**

Rename `IsUserLeadOfTeamAsync` test methods to `IsUserCoordinatorOfTeamAsync`.
Change `TeamMemberRole.Lead` → `.Coordinator` throughout.

- [ ] **Step 3: Update MembershipCalculatorTests**

Change `SystemTeamIds.Leads` → `.Coordinators` and `TeamMemberRole.Lead` → `.Coordinator`.

- [ ] **Step 4: Update ContactFieldServiceTests**

Change `ContactFieldVisibility.LeadsAndBoard` → `.CoordinatorsAndBoard`.
Change `IsLeadRole` → `IsManagement`.

- [ ] **Step 5: Update ConsentServiceTests**

Change `SyncLeadsMembershipForUserAsync` → `SyncCoordinatorsMembershipForUserAsync`.

- [ ] **Step 6: Run all tests**

Run: `dotnet test Humans.slnx`

Fix any failures.

- [ ] **Step 7: Commit**

```bash
git add tests/
git commit -m "test: update tests for Coordinator rename and IsManagement (#130)"
```

### Task 14: Update documentation

**Files:**
- Modify: `docs/features/06-teams.md`
- Modify: `.claude/DATA_MODEL.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update `06-teams.md`**

Replace all "Lead"/"Leads" terminology with "Coordinator"/"Coordinators".
Add section on team hierarchy (departments).
Document `IsManagement` flag behavior.

- [ ] **Step 2: Update `DATA_MODEL.md`**

Update `SystemTeamType` enum values.
Add `IsManagement` to `TeamRoleDefinition`.
Add `ParentTeamId` to `Team`.
Update `SystemTeamIds` constants.

- [ ] **Step 3: Update `CLAUDE.md`**

Update any "Lead" references in project documentation.

- [ ] **Step 4: Commit**

```bash
git add docs/features/06-teams.md .claude/DATA_MODEL.md CLAUDE.md
git commit -m "docs: update documentation for Coordinator rename and team hierarchy (#130)"
```

### Task 15: Final build and integration check

- [ ] **Step 1: Full build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 2: Full test suite**

Run: `dotnet test Humans.slnx`

- [ ] **Step 3: Grep for any remaining "Lead" references (excluding CampLead)**

Run: `grep -r "\.Lead\b" src/ --include="*.cs" | grep -v CampLead | grep -v CampLeadRole`
Run: `grep -r "IsLeadRole" src/ --include="*.cs"`
Run: `grep -r "\"Lead\"" src/ --include="*.cs" | grep -v CampLead`

Fix any stragglers.

- [ ] **Step 4: Final commit if needed**

```bash
git commit -m "fix: address remaining Lead references (#130)"
```
