# Team Role Slots Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add named role slots to teams so members can fill specific positions, with a cross-team roster summary showing staffing gaps.

**Architecture:** Two new entities (`TeamRoleDefinition`, `TeamRoleAssignment`) layered on top of existing `Team`/`TeamMember`. New service methods on `ITeamService`/`TeamService`. New controller actions split between `TeamController` (roster summary) and `TeamAdminController` (role CRUD/assignment). New views for roster display and role management.

**Tech Stack:** ASP.NET Core MVC, EF Core (PostgreSQL), NodaTime, xUnit + NSubstitute + AwesomeAssertions

**Spec:** `docs/superpowers/specs/2026-03-11-team-role-slots-design.md`

---

## File Structure

### New Files

| File | Responsibility |
|------|---------------|
| `src/Humans.Domain/Entities/TeamRoleDefinition.cs` | Role definition entity |
| `src/Humans.Domain/Entities/TeamRoleAssignment.cs` | Role assignment entity |
| `src/Humans.Domain/Enums/SlotPriority.cs` | Priority enum |
| `src/Humans.Infrastructure/Data/Configurations/TeamRoleDefinitionConfiguration.cs` | EF config for definitions |
| `src/Humans.Infrastructure/Data/Configurations/TeamRoleAssignmentConfiguration.cs` | EF config for assignments |
| `src/Humans.Web/Views/Team/Roster.cshtml` | Cross-team roster summary view |
| `src/Humans.Web/Views/TeamAdmin/Roles.cshtml` | Role management page |
| `src/Humans.Web/Views/Team/_RosterSection.cshtml` | Partial for team detail page roster |
| `tests/Humans.Application.Tests/Services/TeamRoleServiceTests.cs` | Unit tests for role slot logic |

### Modified Files

| File | Changes |
|------|---------|
| `src/Humans.Domain/Entities/Team.cs` | Add `RoleDefinitions` navigation property |
| `src/Humans.Domain/Entities/TeamMember.cs` | Add `RoleAssignments` navigation property |
| `src/Humans.Domain/Enums/AuditAction.cs` | Add role slot audit actions |
| `src/Humans.Infrastructure/Data/HumansDbContext.cs` | Add `DbSet` entries |
| `src/Humans.Application/Interfaces/ITeamService.cs` | Add role slot service methods |
| `src/Humans.Infrastructure/Services/TeamService.cs` | Implement role slot methods, modify `LeaveTeamAsync`/`RemoveMemberAsync` |
| `src/Humans.Web/Controllers/TeamController.cs` | Add `Roster` action |
| `src/Humans.Web/Controllers/TeamAdminController.cs` | Add role CRUD and assignment actions |
| `src/Humans.Web/Models/TeamViewModels.cs` | Add role slot view models |
| `src/Humans.Web/Views/Team/Details.cshtml` | Include roster partial |

---

## Chunk 1: Domain Layer

### Task 1: SlotPriority Enum

**Files:**
- Create: `src/Humans.Domain/Enums/SlotPriority.cs`

- [ ] **Step 1: Create the enum**

```csharp
namespace Humans.Domain.Enums;

/// <summary>
/// Priority level for a team role slot.
/// </summary>
public enum SlotPriority
{
    /// <summary>
    /// Must be filled — critical for team function.
    /// </summary>
    Critical = 0,

    /// <summary>
    /// Should be filled if possible.
    /// </summary>
    Important = 1,

    /// <summary>
    /// Helpful but not essential.
    /// </summary>
    NiceToHave = 2
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Domain`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/SlotPriority.cs
git commit -m "feat: add SlotPriority enum"
```

### Task 2: TeamRoleDefinition Entity

**Files:**
- Create: `src/Humans.Domain/Entities/TeamRoleDefinition.cs`
- Modify: `src/Humans.Domain/Entities/Team.cs`

- [ ] **Step 1: Create the entity**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Defines a named role on a team with a configurable number of slots.
/// </summary>
public class TeamRoleDefinition
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the team.
    /// </summary>
    public Guid TeamId { get; init; }

    /// <summary>
    /// Navigation property to the team.
    /// </summary>
    public Team Team { get; set; } = null!;

    /// <summary>
    /// Role name (e.g., "Lead", "Social Media", "Designer").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional markdown-friendly description / job description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Number of slots available for this role (>= 1).
    /// </summary>
    public int SlotCount { get; set; } = 1;

    /// <summary>
    /// CSV of SlotPriority values, one per slot. Length must equal SlotCount.
    /// Stored as e.g. "Critical,Important,NiceToHave".
    /// </summary>
    public List<SlotPriority> Priorities { get; set; } = [];

    /// <summary>
    /// Display ordering within the team.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// When this role definition was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When this role definition was last updated.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Whether this is the reserved "Lead" role definition.
    /// </summary>
    public bool IsLeadRole => string.Equals(Name, "Lead", StringComparison.Ordinal);

    /// <summary>
    /// Navigation property to assignments.
    /// </summary>
    public ICollection<TeamRoleAssignment> Assignments { get; } = new List<TeamRoleAssignment>();
}
```

- [ ] **Step 2: Add navigation property to Team.cs**

Add before the closing brace of the `Team` class, after the `LegalDocuments` navigation:

```csharp
/// <summary>
/// Navigation property to role definitions.
/// </summary>
public ICollection<TeamRoleDefinition> RoleDefinitions { get; } = new List<TeamRoleDefinition>();
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Humans.Domain`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Entities/TeamRoleDefinition.cs src/Humans.Domain/Entities/Team.cs
git commit -m "feat: add TeamRoleDefinition entity"
```

### Task 3: TeamRoleAssignment Entity

**Files:**
- Create: `src/Humans.Domain/Entities/TeamRoleAssignment.cs`
- Modify: `src/Humans.Domain/Entities/TeamMember.cs`

- [ ] **Step 1: Create the entity**

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Assigns a team member to a specific slot in a role definition.
/// </summary>
public class TeamRoleAssignment
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Foreign key to the role definition.
    /// </summary>
    public Guid TeamRoleDefinitionId { get; init; }

    /// <summary>
    /// Navigation property to the role definition.
    /// </summary>
    public TeamRoleDefinition TeamRoleDefinition { get; set; } = null!;

    /// <summary>
    /// Foreign key to the team member.
    /// </summary>
    public Guid TeamMemberId { get; init; }

    /// <summary>
    /// Navigation property to the team member.
    /// </summary>
    public TeamMember TeamMember { get; set; } = null!;

    /// <summary>
    /// 0-based slot index within the role definition.
    /// </summary>
    public int SlotIndex { get; init; }

    /// <summary>
    /// When this assignment was made.
    /// </summary>
    public Instant AssignedAt { get; init; }

    /// <summary>
    /// Foreign key to the user who made the assignment.
    /// </summary>
    public Guid AssignedByUserId { get; init; }

    /// <summary>
    /// Navigation property to the user who made the assignment.
    /// </summary>
    public User AssignedByUser { get; set; } = null!;
}
```

- [ ] **Step 2: Add navigation property to TeamMember.cs**

Add before the closing brace of the `TeamMember` class, after the `IsActive` property:

```csharp
/// <summary>
/// Navigation property to role slot assignments.
/// </summary>
public ICollection<TeamRoleAssignment> RoleAssignments { get; } = new List<TeamRoleAssignment>();
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Humans.Domain`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Entities/TeamRoleAssignment.cs src/Humans.Domain/Entities/TeamMember.cs
git commit -m "feat: add TeamRoleAssignment entity"
```

### Task 4: AuditAction Enum Updates

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs`

- [ ] **Step 1: Add new audit actions**

Add before the closing brace of the `AuditAction` enum:

```csharp
    TeamRoleDefinitionCreated,
    TeamRoleDefinitionUpdated,
    TeamRoleDefinitionDeleted,
    TeamRoleAssigned,
    TeamRoleUnassigned,
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Domain`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/AuditAction.cs
git commit -m "feat: add audit actions for team role slots"
```

---

## Chunk 2: Infrastructure — EF Configuration & Migration

### Task 5: TeamRoleDefinition EF Configuration

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/TeamRoleDefinitionConfiguration.cs`

- [ ] **Step 1: Create the configuration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Infrastructure.Data.Configurations;

public class TeamRoleDefinitionConfiguration : IEntityTypeConfiguration<TeamRoleDefinition>
{
    public void Configure(EntityTypeBuilder<TeamRoleDefinition> builder)
    {
        builder.ToTable("team_role_definitions");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(d => d.Description)
            .HasMaxLength(2000);

        builder.Property(d => d.SlotCount)
            .IsRequired();

        builder.Property(d => d.Priorities)
            .IsRequired()
            .HasMaxLength(500)
            .HasConversion(
                v => string.Join(",", v.Select(p => p.ToString())),
                v => string.IsNullOrEmpty(v)
                    ? new List<SlotPriority>()
                    : v.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => Enum.Parse<SlotPriority>(s))
                        .ToList())
            .HasDefaultValue("");

        builder.Property(d => d.SortOrder)
            .IsRequired();

        builder.Property(d => d.CreatedAt)
            .IsRequired();

        builder.Property(d => d.UpdatedAt)
            .IsRequired();

        // Case-insensitive unique index on (TeamId, Name)
        builder.HasIndex(d => new { d.TeamId, d.Name })
            .IsUnique()
            .HasDatabaseName("IX_team_role_definitions_team_name_unique");

        builder.HasIndex(d => d.TeamId);

        // Relationship: Team -> RoleDefinitions (cascade delete)
        builder.HasOne(d => d.Team)
            .WithMany(t => t.RoleDefinitions)
            .HasForeignKey(d => d.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ignore computed property
        builder.Ignore(d => d.IsLeadRole);
    }
}
```

**Note on case-insensitive index:** EF Core doesn't support `lower()` in index definitions directly. After the migration is generated, manually edit it to use `NpgsqlIndexBuilderExtensions.IsCreatedConcurrently` or raw SQL. Alternatively, add a migration operation:

```csharp
// In the migration Up() method, after CreateTable, replace the auto-generated unique index with:
migrationBuilder.Sql(
    @"CREATE UNIQUE INDEX ""IX_team_role_definitions_team_name_unique""
      ON team_role_definitions (""TeamId"", lower(""Name""))");
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Infrastructure`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/TeamRoleDefinitionConfiguration.cs
git commit -m "feat: add TeamRoleDefinition EF configuration"
```

### Task 6: TeamRoleAssignment EF Configuration

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/TeamRoleAssignmentConfiguration.cs`

- [ ] **Step 1: Create the configuration**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data.Configurations;

public class TeamRoleAssignmentConfiguration : IEntityTypeConfiguration<TeamRoleAssignment>
{
    public void Configure(EntityTypeBuilder<TeamRoleAssignment> builder)
    {
        builder.ToTable("team_role_assignments");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.SlotIndex)
            .IsRequired();

        builder.Property(a => a.AssignedAt)
            .IsRequired();

        // Unique: same person can't be assigned to same role twice
        builder.HasIndex(a => new { a.TeamRoleDefinitionId, a.TeamMemberId })
            .IsUnique()
            .HasDatabaseName("IX_team_role_assignments_definition_member_unique");

        // Unique: same slot can't be filled by two people
        builder.HasIndex(a => new { a.TeamRoleDefinitionId, a.SlotIndex })
            .IsUnique()
            .HasDatabaseName("IX_team_role_assignments_definition_slot_unique");

        builder.HasIndex(a => a.TeamMemberId);

        // Relationship: TeamRoleDefinition -> Assignments (cascade delete)
        builder.HasOne(a => a.TeamRoleDefinition)
            .WithMany(d => d.Assignments)
            .HasForeignKey(a => a.TeamRoleDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Relationship: TeamMember -> RoleAssignments (restrict — app handles cleanup)
        builder.HasOne(a => a.TeamMember)
            .WithMany(m => m.RoleAssignments)
            .HasForeignKey(a => a.TeamMemberId)
            .OnDelete(DeleteBehavior.Restrict);

        // Relationship: AssignedByUser (set null on user deletion)
        builder.HasOne(a => a.AssignedByUser)
            .WithMany()
            .HasForeignKey(a => a.AssignedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Infrastructure`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/TeamRoleAssignmentConfiguration.cs
git commit -m "feat: add TeamRoleAssignment EF configuration"
```

### Task 7: DbContext and Migration

**Files:**
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`
- Create: migration file (auto-generated)

- [ ] **Step 1: Add DbSet entries to HumansDbContext.cs**

Add after the `TeamJoinRequestStateHistories` line:

```csharp
public DbSet<TeamRoleDefinition> TeamRoleDefinitions => Set<TeamRoleDefinition>();
public DbSet<TeamRoleAssignment> TeamRoleAssignments => Set<TeamRoleAssignment>();
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Infrastructure`
Expected: Build succeeded

- [ ] **Step 3: Generate migration**

Run: `dotnet ef migrations add AddTeamRoleSlots --project src/Humans.Infrastructure --startup-project src/Humans.Web`
Expected: Migration file created in `src/Humans.Infrastructure/Migrations/`

- [ ] **Step 4: Edit migration for case-insensitive index**

In the generated migration `Up()` method, find the auto-generated unique index on `(TeamId, Name)` and replace it with raw SQL:

```csharp
// Remove the auto-generated CreateIndex for IX_team_role_definitions_team_name_unique
// Replace with:
migrationBuilder.Sql(
    @"CREATE UNIQUE INDEX ""IX_team_role_definitions_team_name_unique""
      ON team_role_definitions (""TeamId"", lower(""Name""))");
```

In the `Down()` method, make sure the index drop still works:
```csharp
migrationBuilder.DropIndex("IX_team_role_definitions_team_name_unique", "team_role_definitions");
```

- [ ] **Step 5: Apply migration to dev database**

Run: `dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web`
Expected: Database updated successfully

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Infrastructure/Data/HumansDbContext.cs src/Humans.Infrastructure/Migrations/
git commit -m "feat: add TeamRoleSlots migration"
```

---

## Chunk 3: Service Layer

### Task 8: Service Interface

**Files:**
- Modify: `src/Humans.Application/Interfaces/ITeamService.cs`

- [ ] **Step 1: Add role slot methods to ITeamService**

Add the following methods at the end of the interface, before the closing brace:

```csharp
    // ==========================================================================
    // Team Role Definitions
    // ==========================================================================

    /// <summary>
    /// Creates a role definition on a team. Blocked for system teams.
    /// </summary>
    Task<TeamRoleDefinition> CreateRoleDefinitionAsync(
        Guid teamId,
        string name,
        string? description,
        int slotCount,
        List<SlotPriority> priorities,
        int sortOrder,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a role definition. Cannot rename to/from "Lead".
    /// </summary>
    Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId,
        string name,
        string? description,
        int slotCount,
        List<SlotPriority> priorities,
        int sortOrder,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a role definition and its assignments. Cannot delete "Lead" role.
    /// </summary>
    Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all role definitions for a team, including assignments.
    /// </summary>
    Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all role slots across all active non-system teams, for the roster summary.
    /// </summary>
    Task<IReadOnlyList<TeamRoleDefinition>> GetAllRoleDefinitionsAsync(
        CancellationToken cancellationToken = default);

    // ==========================================================================
    // Team Role Assignments
    // ==========================================================================

    /// <summary>
    /// Assigns a user to a role slot. Auto-adds to team if not a member.
    /// For "Lead" roles, also sets TeamMember.Role = Lead.
    /// </summary>
    Task<TeamRoleAssignment> AssignToRoleAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unassigns a member from a role slot.
    /// For "Lead" roles, sets TeamMember.Role = Member if no remaining Lead slots.
    /// </summary>
    Task UnassignFromRoleAsync(
        Guid roleDefinitionId,
        Guid teamMemberId,
        Guid actorUserId,
        CancellationToken cancellationToken = default);
```

You will also need to add `using Humans.Domain.Enums;` if not already present (it should be — `SlotPriority` is in that namespace).

- [ ] **Step 2: Build to verify** (will fail — implementation not yet written, but interface should compile)

Run: `dotnet build src/Humans.Application`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/ITeamService.cs
git commit -m "feat: add role slot methods to ITeamService"
```

### Task 9: Service Implementation — Role Definitions

**Files:**
- Modify: `src/Humans.Infrastructure/Services/TeamService.cs`

- [ ] **Step 1: Implement CreateRoleDefinitionAsync**

Add at the end of `TeamService.cs`, before the private helper methods:

```csharp
    // ==========================================================================
    // Team Role Definitions
    // ==========================================================================

    public async Task<TeamRoleDefinition> CreateRoleDefinitionAsync(
        Guid teamId,
        string name,
        string? description,
        int slotCount,
        List<SlotPriority> priorities,
        int sortOrder,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var team = await _dbContext.Teams.FindAsync(new object[] { teamId }, cancellationToken)
            ?? throw new InvalidOperationException($"Team {teamId} not found");

        if (team.IsSystemTeam)
            throw new InvalidOperationException("Cannot create role definitions on system teams");

        if (slotCount < 1)
            throw new InvalidOperationException("Slot count must be at least 1");

        if (priorities.Count != slotCount)
            throw new InvalidOperationException("Priorities count must equal slot count");

        // "Lead" is reserved — auto-created only
        if (string.Equals(name, "Lead", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("'Lead' is a reserved role name");

        // Check uniqueness (case-insensitive) — DB index enforces this too
        var exists = await _dbContext.Set<TeamRoleDefinition>()
            .AnyAsync(d => d.TeamId == teamId
                && d.Name.ToLower() == name.ToLower(), cancellationToken);
        if (exists)
            throw new InvalidOperationException($"A role named '{name}' already exists on this team");

        var canManage = await CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage roles on this team");

        var now = _clock.GetCurrentInstant();
        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = name,
            Description = description,
            SlotCount = slotCount,
            Priorities = priorities,
            SortOrder = sortOrder,
            CreatedAt = now,
            UpdatedAt = now
        };

        _dbContext.Set<TeamRoleDefinition>().Add(definition);

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionCreated, "Team", teamId,
            $"Role '{name}' created on {team.Name} ({slotCount} slots)",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: definition.Id, relatedEntityType: "TeamRoleDefinition");

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created role definition '{Name}' on team {TeamId}", name, teamId);
        return definition;
    }
```

- [ ] **Step 2: Implement UpdateRoleDefinitionAsync**

```csharp
    public async Task<TeamRoleDefinition> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId,
        string name,
        string? description,
        int slotCount,
        List<SlotPriority> priorities,
        int sortOrder,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage roles on this team");

        if (slotCount < 1)
            throw new InvalidOperationException("Slot count must be at least 1");

        if (priorities.Count != slotCount)
            throw new InvalidOperationException("Priorities count must equal slot count");

        // Cannot rename to or from "Lead"
        if (definition.IsLeadRole && !string.Equals(name, "Lead", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot rename the Lead role");

        if (!definition.IsLeadRole && string.Equals(name, "Lead", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("'Lead' is a reserved role name");

        // Cannot reduce slot count below filled slots
        var filledSlots = definition.Assignments.Count;
        if (slotCount < filledSlots)
            throw new InvalidOperationException(
                $"Cannot reduce slot count to {slotCount} — {filledSlots} slots are currently filled. Unassign members first.");

        // Check name uniqueness if name changed
        if (!string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            var exists = await _dbContext.Set<TeamRoleDefinition>()
                .AnyAsync(d => d.TeamId == definition.TeamId
                    && d.Id != roleDefinitionId
                    && d.Name.ToLower() == name.ToLower(), cancellationToken);
            if (exists)
                throw new InvalidOperationException($"A role named '{name}' already exists on this team");
        }

        definition.Name = name;
        definition.Description = description;
        definition.SlotCount = slotCount;
        definition.Priorities = priorities;
        definition.SortOrder = sortOrder;
        definition.UpdatedAt = _clock.GetCurrentInstant();

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionUpdated, "Team", definition.TeamId,
            $"Role '{name}' updated on {definition.Team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: definition.Id, relatedEntityType: "TeamRoleDefinition");

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated role definition {DefinitionId} on team {TeamId}", roleDefinitionId, definition.TeamId);
        return definition;
    }
```

- [ ] **Step 3: Implement DeleteRoleDefinitionAsync**

```csharp
    public async Task DeleteRoleDefinitionAsync(
        Guid roleDefinitionId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        if (definition.IsLeadRole)
            throw new InvalidOperationException("Cannot delete the Lead role — it is auto-managed");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage roles on this team");

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleDefinitionDeleted, "Team", definition.TeamId,
            $"Role '{definition.Name}' deleted from {definition.Team.Name} ({definition.Assignments.Count} assignments removed)",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: definition.Id, relatedEntityType: "TeamRoleDefinition");

        _dbContext.Set<TeamRoleDefinition>().Remove(definition); // Cascade deletes assignments

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Deleted role definition {DefinitionId} from team {TeamId}", roleDefinitionId, definition.TeamId);
    }
```

- [ ] **Step 4: Implement query methods**

```csharp
    public async Task<IReadOnlyList<TeamRoleDefinition>> GetRoleDefinitionsAsync(
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Assignments)
                .ThenInclude(a => a.TeamMember)
                    .ThenInclude(m => m.User)
            .Where(d => d.TeamId == teamId)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TeamRoleDefinition>> GetAllRoleDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
                .ThenInclude(a => a.TeamMember)
                    .ThenInclude(m => m.User)
            .Where(d => d.Team.IsActive && d.Team.SystemTeamType == SystemTeamType.None)
            .OrderBy(d => d.Team.Name)
            .ThenBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Humans.Infrastructure`
Expected: May fail if AssignToRoleAsync/UnassignFromRoleAsync not yet implemented. That's OK — proceed to next step.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Infrastructure/Services/TeamService.cs
git commit -m "feat: implement role definition CRUD in TeamService"
```

### Task 10: Service Implementation — Role Assignments

**Files:**
- Modify: `src/Humans.Infrastructure/Services/TeamService.cs`

- [ ] **Step 1: Implement AssignToRoleAsync**

```csharp
    // ==========================================================================
    // Team Role Assignments
    // ==========================================================================

    public async Task<TeamRoleAssignment> AssignToRoleAsync(
        Guid roleDefinitionId,
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to assign roles on this team");

        // Find or create team membership (inline to keep everything in one SaveChangesAsync)
        var member = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == definition.TeamId
                && m.UserId == targetUserId && m.LeftAt == null, cancellationToken);

        if (member == null)
        {
            // Auto-add to team (inlined, not calling AddMemberToTeamAsync which has its own SaveChanges)
            member = new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = definition.TeamId,
                UserId = targetUserId,
                Role = TeamMemberRole.Member,
                JoinedAt = _clock.GetCurrentInstant()
            };
            _dbContext.TeamMembers.Add(member);

            // Resolve any pending join request
            var pendingRequest = await _dbContext.TeamJoinRequests
                .FirstOrDefaultAsync(r => r.TeamId == definition.TeamId
                    && r.UserId == targetUserId
                    && r.Status == TeamJoinRequestStatus.Pending, cancellationToken);
            if (pendingRequest != null)
            {
                pendingRequest.Approve(actorUserId, "Added via role assignment", _clock);
            }

            EnqueueGoogleSyncOutboxEvent(
                member.Id, definition.TeamId, targetUserId,
                GoogleSyncOutboxEventTypes.AddUserToTeamResources);

            await _auditLogService.LogAsync(
                AuditAction.TeamMemberAdded, "Team", definition.TeamId,
                $"Auto-added to {definition.Team.Name} via role assignment",
                actorUserId, (await _dbContext.Users.FindAsync([actorUserId], cancellationToken))?.DisplayName ?? actorUserId.ToString(),
                relatedEntityId: targetUserId, relatedEntityType: "User");
        }

        // Check not already assigned to this role
        var alreadyAssigned = definition.Assignments.Any(a => a.TeamMemberId == member.Id);
        if (alreadyAssigned)
            throw new InvalidOperationException("Member is already assigned to this role");

        // Find first open slot
        var takenSlots = definition.Assignments.Select(a => a.SlotIndex).ToHashSet();
        int? openSlot = null;
        for (var i = 0; i < definition.SlotCount; i++)
        {
            if (!takenSlots.Contains(i))
            {
                openSlot = i;
                break;
            }
        }

        if (openSlot == null)
            throw new InvalidOperationException("All slots for this role are filled");

        var now = _clock.GetCurrentInstant();
        var assignment = new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = roleDefinitionId,
            TeamMemberId = member.Id,
            SlotIndex = openSlot.Value,
            AssignedAt = now,
            AssignedByUserId = actorUserId
        };

        _dbContext.Set<TeamRoleAssignment>().Add(assignment);

        // If this is a Lead role, set TeamMember.Role = Lead
        // (Leads system team sync is handled by the controller via ISystemTeamSync,
        //  consistent with SetMemberRoleAsync pattern)
        if (definition.IsLeadRole && member.Role != TeamMemberRole.Lead)
        {
            member.Role = TeamMemberRole.Lead;
        }

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        var targetUser = await _dbContext.Users.FindAsync([targetUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleAssigned, "Team", definition.TeamId,
            $"{targetUser?.DisplayName ?? targetUserId.ToString()} assigned to '{definition.Name}' slot {openSlot.Value + 1} on {definition.Team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: targetUserId, relatedEntityType: "User");

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Assigned user {UserId} to role {RoleId} slot {Slot} on team {TeamId}",
            targetUserId, roleDefinitionId, openSlot.Value, definition.TeamId);
        return assignment;
    }
```

**Note:** Leads system team sync (`SyncLeadsMembershipForUserAsync`) is NOT called from the service — it's called from the controller after the service method returns, consistent with how `SetRole` in `TeamAdminController` handles it (line 232). The service only sets `TeamMember.Role`; the controller calls `_systemTeamSyncJob.SyncLeadsMembershipForUserAsync(userId)` afterward.

- [ ] **Step 2: Implement UnassignFromRoleAsync**

```csharp
    public async Task UnassignFromRoleAsync(
        Guid roleDefinitionId,
        Guid teamMemberId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _dbContext.Set<TeamRoleDefinition>()
            .Include(d => d.Team)
            .Include(d => d.Assignments)
            .FirstOrDefaultAsync(d => d.Id == roleDefinitionId, cancellationToken)
            ?? throw new InvalidOperationException($"Role definition {roleDefinitionId} not found");

        var canManage = await CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            throw new InvalidOperationException("User does not have permission to manage roles on this team");

        var assignment = definition.Assignments.FirstOrDefault(a => a.TeamMemberId == teamMemberId)
            ?? throw new InvalidOperationException("Member is not assigned to this role");

        _dbContext.Set<TeamRoleAssignment>().Remove(assignment);

        // If this is a Lead role, check if member has remaining Lead assignments
        if (definition.IsLeadRole)
        {
            var member = await _dbContext.TeamMembers.FindAsync([teamMemberId], cancellationToken)
                ?? throw new InvalidOperationException("Team member not found");

            var hasOtherLeadAssignments = await _dbContext.Set<TeamRoleAssignment>()
                .AnyAsync(a => a.TeamMemberId == teamMemberId
                    && a.Id != assignment.Id
                    && a.TeamRoleDefinition.Name == "Lead", cancellationToken);

            if (!hasOtherLeadAssignments && member.Role == TeamMemberRole.Lead)
            {
                member.Role = TeamMemberRole.Member;
            }
        }

        var actor = await _dbContext.Users.FindAsync([actorUserId], cancellationToken);
        await _auditLogService.LogAsync(
            AuditAction.TeamRoleUnassigned, "Team", definition.TeamId,
            $"Member unassigned from '{definition.Name}' on {definition.Team.Name}",
            actorUserId, actor?.DisplayName ?? actorUserId.ToString(),
            relatedEntityId: teamMemberId, relatedEntityType: "TeamMember");

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Unassigned member {MemberId} from role {RoleId} on team {TeamId}",
            teamMemberId, roleDefinitionId, definition.TeamId);
    }
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Humans.Infrastructure`
Expected: Build succeeded (may need to check if `EnsureLeadsTeamMembershipAsync` exists — see note in Step 1)

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Services/TeamService.cs
git commit -m "feat: implement role assignment/unassignment in TeamService"
```

### Task 11: Modify LeaveTeamAsync and RemoveMemberAsync

**Files:**
- Modify: `src/Humans.Infrastructure/Services/TeamService.cs`

- [ ] **Step 1: Add role assignment cleanup to LeaveTeamAsync**

In `LeaveTeamAsync` (around line 331), BEFORE `member.LeftAt = _clock.GetCurrentInstant();`, add:

```csharp
        // Clean up role assignments before departure
        var roleAssignments = await _dbContext.Set<TeamRoleAssignment>()
            .Where(a => a.TeamMemberId == member.Id)
            .ToListAsync(cancellationToken);
        if (roleAssignments.Count > 0)
        {
            _dbContext.Set<TeamRoleAssignment>().RemoveRange(roleAssignments);
        }
```

- [ ] **Step 2: Add role assignment cleanup to RemoveMemberAsync**

In `RemoveMemberAsync` (around line 676), BEFORE `member.LeftAt = _clock.GetCurrentInstant();`, add the same cleanup code:

```csharp
        // Clean up role assignments before departure
        var roleAssignments = await _dbContext.Set<TeamRoleAssignment>()
            .Where(a => a.TeamMemberId == member.Id)
            .ToListAsync(cancellationToken);
        if (roleAssignments.Count > 0)
        {
            _dbContext.Set<TeamRoleAssignment>().RemoveRange(roleAssignments);
        }
```

Both methods already call `SaveChangesAsync` after setting `LeftAt`. The DELETEs and UPDATE will be in the same save, with EF Core issuing DELETEs before UPDATEs.

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Humans.Infrastructure`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Services/TeamService.cs
git commit -m "feat: clean up role assignments on team member departure"
```

### Task 12: Reserved Slug Validation

**Files:**
- Modify: `src/Humans.Infrastructure/Services/TeamService.cs`

- [ ] **Step 1: Add reserved slug check**

Find the `CreateTeamAsync` method. After slug generation (`var baseSlug = GenerateSlug(name);`), add:

```csharp
        // Block reserved slugs
        if (string.Equals(baseSlug, "roster", StringComparison.Ordinal))
            throw new InvalidOperationException("The team name 'roster' is reserved");
```

`UpdateTeamAsync` does not regenerate slugs, so no check needed there.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Infrastructure`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Services/TeamService.cs
git commit -m "feat: block reserved slug 'roster' for team creation"
```

### Task 13: Auto-Create Lead Role on Team Creation

**Files:**
- Modify: `src/Humans.Infrastructure/Services/TeamService.cs`

- [ ] **Step 1: Add Lead role creation to CreateTeamAsync**

In `CreateTeamAsync`, after the team is added to the context and before `SaveChangesAsync`, add:

```csharp
            // Auto-create Lead role definition for non-system teams
            var leadRole = new TeamRoleDefinition
            {
                Id = Guid.NewGuid(),
                TeamId = team.Id,
                Name = "Lead",
                Description = "Team leadership role",
                SlotCount = 1,
                Priorities = [SlotPriority.Critical],
                SortOrder = 0,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.Set<TeamRoleDefinition>().Add(leadRole);
```

Make sure `using Humans.Domain.Enums;` includes `SlotPriority`.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Infrastructure`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Services/TeamService.cs
git commit -m "feat: auto-create Lead role definition on team creation"
```

---

## Chunk 4: Unit Tests

### Task 14: Role Definition Tests

**Files:**
- Create: `tests/Humans.Application.Tests/Services/TeamRoleServiceTests.cs`

- [ ] **Step 1: Create test class with setup**

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class TeamRoleServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly TeamService _service;

    public TeamRoleServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 11, 12, 0));
        _service = new TeamService(
            _dbContext,
            Substitute.For<IAuditLogService>(),
            Substitute.For<IEmailService>(),
            _clock,
            NullLogger<TeamService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private User SeedUser(string displayName = "Test User")
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en"
        };
        _dbContext.Users.Add(user);
        return user;
    }

    private Team SeedTeam(string name = "Test Team")
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            SystemTeamType = SystemTeamType.None,
            IsActive = true,
            RequiresApproval = false,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(team);
        return team;
    }

    private TeamMember SeedMember(Team team, User user, TeamMemberRole role = TeamMemberRole.Member)
    {
        var member = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Role = role,
            JoinedAt = _clock.GetCurrentInstant()
        };
        _dbContext.TeamMembers.Add(member);
        return member;
    }

    private TeamRoleDefinition SeedRoleDefinition(Team team, string name = "Designer",
        int slotCount = 2, int sortOrder = 1)
    {
        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Name = name,
            SlotCount = slotCount,
            Priorities = Enumerable.Range(0, slotCount)
                .Select(i => i == 0 ? SlotPriority.Critical : SlotPriority.Important)
                .ToList(),
            SortOrder = sortOrder,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Set<TeamRoleDefinition>().Add(definition);
        return definition;
    }

    private void SeedAdminRole(User user)
    {
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleName = RoleNames.Admin,
            ValidFrom = _clock.GetCurrentInstant() - Duration.FromDays(1),
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = user.Id
        });
    }
```

- [ ] **Step 2: Write failing test — create role definition on system team is blocked**

```csharp
    [Fact]
    public async Task CreateRoleDefinitionAsync_SystemTeam_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var systemTeam = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Volunteers",
            Slug = "volunteers",
            SystemTeamType = SystemTeamType.Volunteers,
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(systemTeam);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.CreateRoleDefinitionAsync(
            systemTeam.Id, "Role", null, 1, [SlotPriority.Critical], 0, admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");
    }
```

- [ ] **Step 3: Run test to verify it passes**

Run: `dotnet test tests/Humans.Application.Tests --filter "TeamRoleServiceTests.CreateRoleDefinitionAsync_SystemTeam_Throws" -v n`
Expected: PASS

- [ ] **Step 4: Write test — create role definition succeeds**

```csharp
    [Fact]
    public async Task CreateRoleDefinitionAsync_ValidInput_CreatesDefinition()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam();
        await _dbContext.SaveChangesAsync();

        var result = await _service.CreateRoleDefinitionAsync(
            team.Id, "Designer", "Makes things pretty", 2,
            [SlotPriority.Critical, SlotPriority.Important], 1, admin.Id);

        result.Should().NotBeNull();
        result.Name.Should().Be("Designer");
        result.SlotCount.Should().Be(2);
        result.Priorities.Should().HaveCount(2);
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Humans.Application.Tests --filter "TeamRoleServiceTests.CreateRoleDefinitionAsync_ValidInput" -v n`
Expected: PASS

- [ ] **Step 6: Write test — "Lead" name is reserved**

```csharp
    [Fact]
    public async Task CreateRoleDefinitionAsync_LeadName_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam();
        await _dbContext.SaveChangesAsync();

        var act = () => _service.CreateRoleDefinitionAsync(
            team.Id, "Lead", null, 1, [SlotPriority.Critical], 0, admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reserved*");
    }
```

- [ ] **Step 7: Write test — delete Lead role is blocked**

```csharp
    [Fact]
    public async Task DeleteRoleDefinitionAsync_LeadRole_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam();
        var leadDef = SeedRoleDefinition(team, "Lead", 1, 0);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.DeleteRoleDefinitionAsync(leadDef.Id, admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Lead*");
    }
```

- [ ] **Step 8: Run all tests**

Run: `dotnet test tests/Humans.Application.Tests --filter "TeamRoleServiceTests" -v n`
Expected: All pass

- [ ] **Step 9: Commit**

```bash
git add tests/Humans.Application.Tests/Services/TeamRoleServiceTests.cs
git commit -m "test: add role definition unit tests"
```

### Task 15: Role Assignment Tests

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/TeamRoleServiceTests.cs`

- [ ] **Step 1: Write test — assign to role succeeds**

```csharp
    [Fact]
    public async Task AssignToRoleAsync_ValidMember_CreatesAssignment()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam();
        var user = SeedUser("Alice");
        var member = SeedMember(team, user);
        var roleDef = SeedRoleDefinition(team, "Designer", 2);
        await _dbContext.SaveChangesAsync();

        var result = await _service.AssignToRoleAsync(roleDef.Id, user.Id, admin.Id);

        result.Should().NotBeNull();
        result.SlotIndex.Should().Be(0);
        result.TeamMemberId.Should().Be(member.Id);
    }
```

- [ ] **Step 2: Write test — assign non-member auto-adds to team**

```csharp
    [Fact]
    public async Task AssignToRoleAsync_NonMember_AutoAddsToTeam()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam();
        var user = SeedUser("Bob");
        // NOT adding user as team member
        var roleDef = SeedRoleDefinition(team, "Designer", 2);
        await _dbContext.SaveChangesAsync();

        var result = await _service.AssignToRoleAsync(roleDef.Id, user.Id, admin.Id);

        result.Should().NotBeNull();
        // Verify user is now a team member
        var membership = await _dbContext.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == team.Id && m.UserId == user.Id && m.LeftAt == null);
        membership.Should().NotBeNull();
    }
```

- [ ] **Step 3: Write test — assign to full role throws**

```csharp
    [Fact]
    public async Task AssignToRoleAsync_AllSlotsFilled_Throws()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam();
        var roleDef = SeedRoleDefinition(team, "Designer", 1);
        var user1 = SeedUser("Alice");
        var member1 = SeedMember(team, user1);
        await _dbContext.SaveChangesAsync();

        await _service.AssignToRoleAsync(roleDef.Id, user1.Id, admin.Id);

        var user2 = SeedUser("Bob");
        SeedMember(team, user2);
        await _dbContext.SaveChangesAsync();

        var act = () => _service.AssignToRoleAsync(roleDef.Id, user2.Id, admin.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*slots*filled*");
    }
```

- [ ] **Step 4: Write test — assign to Lead role sets TeamMember.Role**

```csharp
    [Fact]
    public async Task AssignToRoleAsync_LeadRole_SetsTeamMemberRoleToLead()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam();
        var user = SeedUser("Alice");
        var member = SeedMember(team, user);
        var leadDef = SeedRoleDefinition(team, "Lead", 2, 0);
        await _dbContext.SaveChangesAsync();

        await _service.AssignToRoleAsync(leadDef.Id, user.Id, admin.Id);

        var updated = await _dbContext.TeamMembers.FindAsync(member.Id);
        updated!.Role.Should().Be(TeamMemberRole.Lead);
    }
```

- [ ] **Step 5: Write test — member departure cleans up assignments**

```csharp
    [Fact]
    public async Task LeaveTeamAsync_CleansUpRoleAssignments()
    {
        var admin = SeedUser("Admin");
        SeedAdminRole(admin);
        var team = SeedTeam();
        var user = SeedUser("Alice");
        var member = SeedMember(team, user);
        var roleDef = SeedRoleDefinition(team);
        await _dbContext.SaveChangesAsync();

        await _service.AssignToRoleAsync(roleDef.Id, user.Id, admin.Id);

        await _service.LeaveTeamAsync(team.Id, user.Id);

        var assignments = await _dbContext.Set<TeamRoleAssignment>()
            .Where(a => a.TeamMemberId == member.Id)
            .ToListAsync();
        assignments.Should().BeEmpty();
    }
```

- [ ] **Step 6: Run all tests**

Run: `dotnet test tests/Humans.Application.Tests --filter "TeamRoleServiceTests" -v n`
Expected: All pass

- [ ] **Step 7: Commit**

```bash
git add tests/Humans.Application.Tests/Services/TeamRoleServiceTests.cs
git commit -m "test: add role assignment unit tests"
```

---

## Chunk 5: View Models and Controllers

### Task 16: View Models

**Files:**
- Modify: `src/Humans.Web/Models/TeamViewModels.cs`

- [ ] **Step 1: Add role slot view models**

Add at the end of the file:

```csharp
public class TeamRoleDefinitionViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SlotCount { get; set; }
    public List<TeamRoleSlotViewModel> Slots { get; set; } = [];
    public int SortOrder { get; set; }
    public bool IsLeadRole { get; set; }
}

public class TeamRoleSlotViewModel
{
    public int SlotIndex { get; set; }
    public string Priority { get; set; } = string.Empty;
    public string PriorityBadgeClass { get; set; } = string.Empty;
    public bool IsFilled { get; set; }
    public Guid? AssignedUserId { get; set; }
    public string? AssignedUserName { get; set; }
    public string? AssignedUserProfilePictureUrl { get; set; }
    public Guid? TeamMemberId { get; set; }
}

public class RoleManagementViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsSystemTeam { get; set; }
    public bool CanManage { get; set; }
    public List<TeamRoleDefinitionViewModel> RoleDefinitions { get; set; } = [];
    public List<TeamMemberViewModel> TeamMembers { get; set; } = [];
}

public class CreateRoleDefinitionModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SlotCount { get; set; } = 1;
    public string Priorities { get; set; } = "Critical";
    public int SortOrder { get; set; }
}

public class EditRoleDefinitionModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SlotCount { get; set; }
    public string Priorities { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class AssignRoleModel
{
    public Guid UserId { get; set; }
}

public class RosterSummaryViewModel
{
    public List<RosterSlotViewModel> Slots { get; set; } = [];
    public string? PriorityFilter { get; set; }
    public string? StatusFilter { get; set; }
}

public class RosterSlotViewModel
{
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public int SlotNumber { get; set; }
    public string Priority { get; set; } = string.Empty;
    public string PriorityBadgeClass { get; set; } = string.Empty;
    public bool IsFilled { get; set; }
    public string? AssignedUserName { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/TeamViewModels.cs
git commit -m "feat: add role slot view models"
```

### Task 17: TeamController — Roster Summary

**Files:**
- Modify: `src/Humans.Web/Controllers/TeamController.cs`

- [ ] **Step 1: Add Roster action**

Add a new action method in `TeamController` (place it near other static-path actions like `Birthdays`, `Map`, etc.):

```csharp
    [HttpGet("Roster")]
    public async Task<IActionResult> Roster(string? priority, string? status)
    {
        var definitions = await _teamService.GetAllRoleDefinitionsAsync();

        var slots = new List<RosterSlotViewModel>();
        foreach (var def in definitions)
        {
            for (var i = 0; i < def.SlotCount; i++)
            {
                var assignment = def.Assignments.FirstOrDefault(a => a.SlotIndex == i);
                var slotPriority = i < def.Priorities.Count ? def.Priorities[i] : SlotPriority.NiceToHave;
                var priorityStr = slotPriority.ToString();

                slots.Add(new RosterSlotViewModel
                {
                    TeamName = def.Team.Name,
                    TeamSlug = def.Team.Slug,
                    RoleName = def.Name,
                    SlotNumber = i + 1,
                    Priority = priorityStr,
                    PriorityBadgeClass = slotPriority switch
                    {
                        SlotPriority.Critical => "bg-danger",
                        SlotPriority.Important => "bg-warning text-dark",
                        _ => "bg-secondary"
                    },
                    IsFilled = assignment != null,
                    AssignedUserName = assignment?.TeamMember?.User?.DisplayName
                });
            }
        }

        // Apply filters
        if (!string.IsNullOrEmpty(priority))
            slots = slots.Where(s => string.Equals(s.Priority, priority, StringComparison.OrdinalIgnoreCase)).ToList();

        if (string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase))
            slots = slots.Where(s => !s.IsFilled).ToList();
        else if (string.Equals(status, "Filled", StringComparison.OrdinalIgnoreCase))
            slots = slots.Where(s => s.IsFilled).ToList();

        // Sort: Critical first, then Important, then NiceToHave, then by team
        slots = slots
            .OrderBy(s => s.Priority switch
            {
                "Critical" => 0,
                "Important" => 1,
                _ => 2
            })
            .ThenBy(s => s.TeamName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.RoleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SlotNumber)
            .ToList();

        var model = new RosterSummaryViewModel
        {
            Slots = slots,
            PriorityFilter = priority,
            StatusFilter = status
        };

        return View(model);
    }
```

You'll need to add `using Humans.Domain.Enums;` if not already present.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeded (view not yet created — that's OK)

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/TeamController.cs
git commit -m "feat: add Roster summary action to TeamController"
```

### Task 18: TeamAdminController — Role Management Actions

**Files:**
- Modify: `src/Humans.Web/Controllers/TeamAdminController.cs`

- [ ] **Step 1: Add Roles management action**

```csharp
    [HttpGet("Roles")]
    public async Task<IActionResult> Roles(string slug)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null) return NotFound();

        var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
        if (!canManage) return Forbid();

        var definitions = await _teamService.GetRoleDefinitionsAsync(team.Id);
        var members = await _teamService.GetTeamMembersAsync(team.Id);

        var model = new RoleManagementViewModel
        {
            TeamId = team.Id,
            TeamName = team.Name,
            Slug = team.Slug,
            IsSystemTeam = team.IsSystemTeam,
            CanManage = canManage,
            RoleDefinitions = definitions.Select(d => MapToViewModel(d)).ToList(),
            TeamMembers = members.Where(m => m.LeftAt == null).Select(m => new TeamMemberViewModel
            {
                UserId = m.UserId,
                DisplayName = m.User?.DisplayName ?? "Unknown",
                Email = m.User?.Email ?? "",
                Role = m.Role.ToString(),
                IsLead = m.Role == TeamMemberRole.Lead,
                JoinedAt = m.JoinedAt.ToDateTimeUtc()
            }).ToList()
        };

        return View(model);
    }

    private static TeamRoleDefinitionViewModel MapToViewModel(TeamRoleDefinition d)
    {
        var slots = new List<TeamRoleSlotViewModel>();
        for (var i = 0; i < d.SlotCount; i++)
        {
            var assignment = d.Assignments.FirstOrDefault(a => a.SlotIndex == i);
            var priority = i < d.Priorities.Count ? d.Priorities[i] : SlotPriority.NiceToHave;

            slots.Add(new TeamRoleSlotViewModel
            {
                SlotIndex = i,
                Priority = priority.ToString(),
                PriorityBadgeClass = priority switch
                {
                    SlotPriority.Critical => "bg-danger",
                    SlotPriority.Important => "bg-warning text-dark",
                    _ => "bg-secondary"
                },
                IsFilled = assignment != null,
                AssignedUserId = assignment?.TeamMember?.UserId,
                AssignedUserName = assignment?.TeamMember?.User?.DisplayName,
                TeamMemberId = assignment?.TeamMemberId
            });
        }

        return new TeamRoleDefinitionViewModel
        {
            Id = d.Id,
            Name = d.Name,
            Description = d.Description,
            SlotCount = d.SlotCount,
            Slots = slots,
            SortOrder = d.SortOrder,
            IsLeadRole = d.IsLeadRole
        };
    }
```

- [ ] **Step 2: Add Create, Edit, Delete actions**

```csharp
    [HttpPost("Roles")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRole(string slug, CreateRoleDefinitionModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null) return NotFound();

        try
        {
            var priorities = model.Priorities
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Enum.Parse<SlotPriority>(s.Trim()))
                .ToList();

            await _teamService.CreateRoleDefinitionAsync(
                team.Id, model.Name, model.Description,
                model.SlotCount, priorities, model.SortOrder, user.Id);

            TempData["SuccessMessage"] = $"Role '{model.Name}' created.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpPost("Roles/{roleId}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRole(string slug, Guid roleId, EditRoleDefinitionModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        try
        {
            var priorities = model.Priorities
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Enum.Parse<SlotPriority>(s.Trim()))
                .ToList();

            await _teamService.UpdateRoleDefinitionAsync(
                roleId, model.Name, model.Description,
                model.SlotCount, priorities, model.SortOrder, user.Id);

            TempData["SuccessMessage"] = $"Role '{model.Name}' updated.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpPost("Roles/{roleId}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRole(string slug, Guid roleId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        try
        {
            await _teamService.DeleteRoleDefinitionAsync(roleId, user.Id);
            TempData["SuccessMessage"] = "Role deleted.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }
```

- [ ] **Step 3: Add Assign/Unassign actions**

```csharp
    [HttpPost("Roles/{roleId}/Assign")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignRole(string slug, Guid roleId, AssignRoleModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        try
        {
            var assignment = await _teamService.AssignToRoleAsync(roleId, model.UserId, user.Id);

            // Sync Leads system team if this was a Lead role assignment
            await _systemTeamSyncJob.SyncLeadsMembershipForUserAsync(model.UserId);

            TempData["SuccessMessage"] = "Member assigned to role.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpPost("Roles/{roleId}/Unassign/{memberId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnassignRole(string slug, Guid roleId, Guid memberId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        try
        {
            // Get the member's UserId before unassigning (for Leads sync)
            // Note: GetTeamMemberByIdAsync doesn't exist yet — either add it to
            // ITeamService, or look up directly: query the assignment to get the UserId
            // before deletion. Simplest: pass userId through the unassign response.
            var memberRecord = await _teamService.GetTeamMembersAsync(
                (await _teamService.GetTeamBySlugAsync(slug))!.Id);
            var member = memberRecord.FirstOrDefault(m => m.Id == memberId);
            await _teamService.UnassignFromRoleAsync(roleId, memberId, user.Id);

            // Sync Leads system team if this was a Lead role unassignment
            if (member != null)
                await _systemTeamSyncJob.SyncLeadsMembershipForUserAsync(member.UserId);

            TempData["SuccessMessage"] = "Member unassigned from role.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Roles), new { slug });
    }

    [HttpGet("Roles/SearchMembers")]
    public async Task<IActionResult> SearchMembersForRole(string slug, string q)
    {
        if (string.IsNullOrEmpty(q) || q.Length < 2)
            return Json(Array.Empty<object>());

        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null) return NotFound();

        var canManage = await _teamService.CanUserApproveRequestsForTeamAsync(team.Id, user.Id);
        if (!canManage) return Forbid();

        // Search team members first, then all users
        var teamMembers = await _teamService.GetTeamMembersAsync(team.Id);
        var activeMembers = teamMembers.Where(m => m.LeftAt == null).ToList();

        var results = activeMembers
            .Where(m => m.User != null &&
                (m.User.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 (m.User.Email != null && m.User.Email.Contains(q, StringComparison.OrdinalIgnoreCase))))
            .Select(m => new { m.User!.Id, m.User.DisplayName, m.User.Email, OnTeam = true })
            .ToList();

        // If fewer than 10 results, also search non-members
        if (results.Count < 10)
        {
            var memberUserIds = activeMembers.Select(m => m.UserId).ToHashSet();
            var otherUsers = await _profileService.SearchApprovedUsersAsync(q);
            var additional = otherUsers
                .Where(u => !memberUserIds.Contains(u.Id))
                .Take(10 - results.Count)
                .Select(u => new { u.Id, u.DisplayName, u.Email, OnTeam = false });
            results.AddRange(additional);
        }

        return Json(results);
    }
```

**Note:** Uses `SearchApprovedUsersAsync` from `IProfileService` (same as the existing `TeamAdminController.SearchUsers` endpoint).

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/TeamAdminController.cs
git commit -m "feat: add role management actions to TeamAdminController"
```

---

## Chunk 6: Views

### Task 19: Roster Section Partial (Team Detail Page)

**Files:**
- Create: `src/Humans.Web/Views/Team/_RosterSection.cshtml`
- Modify: `src/Humans.Web/Views/Team/Details.cshtml`

- [ ] **Step 1: Create the roster partial**

Create `src/Humans.Web/Views/Team/_RosterSection.cshtml`. This partial receives a list of `TeamRoleDefinitionViewModel` and a `bool CanManage` via `ViewData`:

```html
@model List<Humans.Web.Models.TeamRoleDefinitionViewModel>

@if (Model.Any())
{
    <div class="card mb-4">
        <div class="card-header d-flex justify-content-between align-items-center">
            <h5 class="mb-0">Roles</h5>
            @if ((bool)(ViewData["CanManage"] ?? false))
            {
                <a href="@Url.Action("Roles", "TeamAdmin", new { slug = ViewData["Slug"] })"
                   class="btn btn-sm btn-outline-primary">Edit Roles</a>
            }
        </div>
        <div class="card-body p-0">
            <table class="table table-hover mb-0">
                <thead>
                    <tr>
                        <th>Role</th>
                        <th>Slot</th>
                        <th>Priority</th>
                        <th>Assigned To</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var role in Model)
                    {
                        @foreach (var slot in role.Slots)
                        {
                            <tr>
                                @if (slot.SlotIndex == 0)
                                {
                                    <td rowspan="@role.Slots.Count">
                                        <strong>@role.Name</strong>
                                        @if (!string.IsNullOrEmpty(role.Description))
                                        {
                                            <br /><small class="text-muted">@role.Description</small>
                                        }
                                    </td>
                                }
                                <td>@(slot.SlotIndex + 1)</td>
                                <td><span class="badge @slot.PriorityBadgeClass">@slot.Priority</span></td>
                                <td>
                                    @if (slot.IsFilled)
                                    {
                                        @slot.AssignedUserName
                                    }
                                    else
                                    {
                                        <span class="text-muted fst-italic">Open</span>
                                    }
                                </td>
                            </tr>
                        }
                    }
                </tbody>
            </table>
        </div>
    </div>
}
```

- [ ] **Step 2: Add roster data to TeamDetailViewModel**

In `TeamViewModels.cs`, add to `TeamDetailViewModel`:

```csharp
public List<TeamRoleDefinitionViewModel> RoleDefinitions { get; set; } = [];
```

- [ ] **Step 3: Include partial in Details.cshtml**

Find the appropriate spot in `Details.cshtml` (after the members section) and add:

```html
@await Html.PartialAsync("_RosterSection", Model.RoleDefinitions,
    new ViewDataDictionary(ViewData) {
        { "CanManage", Model.CanCurrentUserManage },
        { "Slug", Model.Slug }
    })
```

- [ ] **Step 4: Update the Details action in TeamController**

In the `Details` action, after building the `TeamDetailViewModel`, add the roster data. Load role definitions and map them:

```csharp
var roleDefinitions = await _teamService.GetRoleDefinitionsAsync(team.Id);
// Map to view models (reuse or inline the mapping logic)
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Views/Team/_RosterSection.cshtml src/Humans.Web/Views/Team/Details.cshtml src/Humans.Web/Models/TeamViewModels.cs src/Humans.Web/Controllers/TeamController.cs
git commit -m "feat: add roster section to team detail page"
```

### Task 20: Roster Summary View

**Files:**
- Create: `src/Humans.Web/Views/Team/Roster.cshtml`

- [ ] **Step 1: Create the roster summary view**

```html
@model Humans.Web.Models.RosterSummaryViewModel

@{
    ViewData["Title"] = "Team Roster";
}

<h2>Team Roster</h2>

<div class="mb-3 d-flex gap-2">
    <div class="dropdown">
        <button class="btn btn-outline-secondary dropdown-toggle" type="button" data-bs-toggle="dropdown">
            Priority: @(Model.PriorityFilter ?? "All")
        </button>
        <ul class="dropdown-menu">
            <li><a class="dropdown-item" href="@Url.Action("Roster", new { status = Model.StatusFilter })">All</a></li>
            <li><a class="dropdown-item" href="@Url.Action("Roster", new { priority = "Critical", status = Model.StatusFilter })">Critical</a></li>
            <li><a class="dropdown-item" href="@Url.Action("Roster", new { priority = "Important", status = Model.StatusFilter })">Important</a></li>
            <li><a class="dropdown-item" href="@Url.Action("Roster", new { priority = "NiceToHave", status = Model.StatusFilter })">Nice to Have</a></li>
        </ul>
    </div>
    <div class="dropdown">
        <button class="btn btn-outline-secondary dropdown-toggle" type="button" data-bs-toggle="dropdown">
            Status: @(Model.StatusFilter ?? "All")
        </button>
        <ul class="dropdown-menu">
            <li><a class="dropdown-item" href="@Url.Action("Roster", new { priority = Model.PriorityFilter })">All</a></li>
            <li><a class="dropdown-item" href="@Url.Action("Roster", new { priority = Model.PriorityFilter, status = "Open" })">Open</a></li>
            <li><a class="dropdown-item" href="@Url.Action("Roster", new { priority = Model.PriorityFilter, status = "Filled" })">Filled</a></li>
        </ul>
    </div>
</div>

@if (!Model.Slots.Any())
{
    <div class="alert alert-info">No role slots defined across teams.</div>
}
else
{
    <table class="table table-hover">
        <thead>
            <tr>
                <th>Team</th>
                <th>Role</th>
                <th>Slot</th>
                <th>Priority</th>
                <th>Assigned To</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var slot in Model.Slots)
            {
                <tr class="@(slot is { IsFilled: false, Priority: "Critical" } ? "table-danger" : "")">
                    <td><a href="@Url.Action("Details", "Team", new { slug = slot.TeamSlug })">@slot.TeamName</a></td>
                    <td>@slot.RoleName</td>
                    <td>@slot.SlotNumber</td>
                    <td><span class="badge @slot.PriorityBadgeClass">@slot.Priority</span></td>
                    <td>
                        @if (slot.IsFilled)
                        {
                            @slot.AssignedUserName
                        }
                        else
                        {
                            <span class="text-muted fst-italic">Open</span>
                        }
                    </td>
                </tr>
            }
        </tbody>
    </table>
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Team/Roster.cshtml
git commit -m "feat: add cross-team roster summary view"
```

### Task 21: Role Management View

**Files:**
- Create: `src/Humans.Web/Views/TeamAdmin/Roles.cshtml`

- [ ] **Step 1: Create the role management view**

This is the most complex view. It needs forms for creating/editing/deleting role definitions and assigning/unassigning members. Create `src/Humans.Web/Views/TeamAdmin/Roles.cshtml`:

```html
@model Humans.Web.Models.RoleManagementViewModel

@{
    ViewData["Title"] = $"Manage Roles - {Model.TeamName}";
}

<h2>Manage Roles — @Model.TeamName</h2>

<p><a href="@Url.Action("Details", "Team", new { slug = Model.Slug })">Back to team</a></p>

@if (TempData["SuccessMessage"] != null)
{
    <div class="alert alert-success">@TempData["SuccessMessage"]</div>
}
@if (TempData["ErrorMessage"] != null)
{
    <div class="alert alert-danger">@TempData["ErrorMessage"]</div>
}

@foreach (var role in Model.RoleDefinitions)
{
    <div class="card mb-3">
        <div class="card-header d-flex justify-content-between align-items-center">
            <strong>@role.Name</strong>
            @if (!role.IsLeadRole)
            {
                <form method="post" asp-action="DeleteRole" asp-route-slug="@Model.Slug"
                      asp-route-roleId="@role.Id"
                      onsubmit="return confirm('Delete role @role.Name and all its assignments?')">
                    @Html.AntiForgeryToken()
                    <button type="submit" class="btn btn-sm btn-outline-danger">Delete</button>
                </form>
            }
        </div>
        <div class="card-body">
            <form method="post" asp-action="EditRole" asp-route-slug="@Model.Slug"
                  asp-route-roleId="@role.Id">
                @Html.AntiForgeryToken()
                <div class="row mb-2">
                    <div class="col-md-3">
                        <label class="form-label">Name</label>
                        <input type="text" name="Name" class="form-control" value="@role.Name"
                               @(role.IsLeadRole ? "readonly" : "") />
                    </div>
                    <div class="col-md-2">
                        <label class="form-label">Slots</label>
                        <input type="number" name="SlotCount" class="form-control"
                               value="@role.SlotCount" min="1" />
                    </div>
                    <div class="col-md-3">
                        <label class="form-label">Priorities (CSV)</label>
                        <input type="text" name="Priorities" class="form-control"
                               value="@string.Join(",", role.Slots.Select(s => s.Priority))" />
                    </div>
                    <div class="col-md-2">
                        <label class="form-label">Sort Order</label>
                        <input type="number" name="SortOrder" class="form-control"
                               value="@role.SortOrder" />
                    </div>
                    <div class="col-md-2 d-flex align-items-end">
                        <button type="submit" class="btn btn-primary">Save</button>
                    </div>
                </div>
                <div class="mb-2">
                    <label class="form-label">Description (markdown)</label>
                    <textarea name="Description" class="form-control" rows="2">@role.Description</textarea>
                </div>
            </form>

            <h6 class="mt-3">Slots</h6>
            <table class="table table-sm">
                <thead>
                    <tr>
                        <th>#</th>
                        <th>Priority</th>
                        <th>Assigned To</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var slot in role.Slots)
                    {
                        <tr>
                            <td>@(slot.SlotIndex + 1)</td>
                            <td><span class="badge @slot.PriorityBadgeClass">@slot.Priority</span></td>
                            <td>
                                @if (slot.IsFilled)
                                {
                                    @slot.AssignedUserName
                                }
                                else
                                {
                                    <span class="text-muted">Open</span>
                                }
                            </td>
                            <td>
                                @if (slot.IsFilled)
                                {
                                    <form method="post" asp-action="UnassignRole"
                                          asp-route-slug="@Model.Slug"
                                          asp-route-roleId="@role.Id"
                                          asp-route-memberId="@slot.TeamMemberId"
                                          style="display:inline">
                                        @Html.AntiForgeryToken()
                                        <button type="submit" class="btn btn-sm btn-outline-warning">Unassign</button>
                                    </form>
                                }
                                else
                                {
                                    <form method="post" asp-action="AssignRole"
                                          asp-route-slug="@Model.Slug"
                                          asp-route-roleId="@role.Id"
                                          class="d-inline-flex gap-1">
                                        @Html.AntiForgeryToken()
                                        <select name="UserId" class="form-select form-select-sm" style="width: auto;">
                                            <option value="">Select member...</option>
                                            @foreach (var member in Model.TeamMembers)
                                            {
                                                <option value="@member.UserId">@member.DisplayName</option>
                                            }
                                        </select>
                                        <button type="submit" class="btn btn-sm btn-outline-success">Assign</button>
                                    </form>
                                }
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    </div>
}

<div class="card">
    <div class="card-header"><strong>Add New Role</strong></div>
    <div class="card-body">
        <form method="post" asp-action="CreateRole" asp-route-slug="@Model.Slug">
            @Html.AntiForgeryToken()
            <div class="row mb-2">
                <div class="col-md-3">
                    <label class="form-label">Name</label>
                    <input type="text" name="Name" class="form-control" required />
                </div>
                <div class="col-md-2">
                    <label class="form-label">Slots</label>
                    <input type="number" name="SlotCount" class="form-control" value="1" min="1" />
                </div>
                <div class="col-md-3">
                    <label class="form-label">Priorities (CSV)</label>
                    <input type="text" name="Priorities" class="form-control" value="Critical" />
                </div>
                <div class="col-md-2">
                    <label class="form-label">Sort Order</label>
                    <input type="number" name="SortOrder" class="form-control" value="10" />
                </div>
            </div>
            <div class="mb-2">
                <label class="form-label">Description (markdown)</label>
                <textarea name="Description" class="form-control" rows="2"></textarea>
            </div>
            <button type="submit" class="btn btn-success">Create Role</button>
        </form>
    </div>
</div>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/TeamAdmin/Roles.cshtml
git commit -m "feat: add role management view"
```

---

## Chunk 7: Integration and Final Verification

### Task 22: Full Build and Test

- [ ] **Step 1: Build entire solution**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test Humans.slnx -v n`
Expected: All tests pass

- [ ] **Step 3: Fix any issues found**

Address compiler errors or test failures.

- [ ] **Step 4: Commit any fixes**

```bash
git add -A
git commit -m "fix: resolve build/test issues from team role slots implementation"
```

### Task 23: Add Navigation Link

**Files:**
- Modify: relevant layout/nav partial (find the existing Teams nav link)

- [ ] **Step 1: Find and update navigation**

Find where team-related nav links exist (likely in a shared layout or nav partial). Add a "Roster" link alongside existing team links:

```html
<a class="dropdown-item" href="@Url.Action("Roster", "Team")">Roster</a>
```

- [ ] **Step 2: Commit**

```bash
git add <nav-file>
git commit -m "feat: add Roster link to navigation"
```

### Task 24: Update Documentation

**Files:**
- Modify: `docs/features/06-teams.md`
- Modify: `.claude/DATA_MODEL.md`

- [ ] **Step 1: Update teams feature doc**

Add a new section to `docs/features/06-teams.md` documenting the role slots feature.

- [ ] **Step 2: Update data model doc**

Add `TeamRoleDefinition` and `TeamRoleAssignment` to `.claude/DATA_MODEL.md`.

- [ ] **Step 3: Commit**

```bash
git add docs/features/06-teams.md .claude/DATA_MODEL.md
git commit -m "docs: update feature spec and data model for team role slots"
```

### Task 25: Create Migration for Existing Teams (Lead Role Seed)

**Files:**
- Create: new migration or data seed

- [ ] **Step 1: Create a migration to seed Lead role definitions for existing non-system teams**

Existing teams won't have a Lead role definition since they were created before this feature. Add a migration that:

```csharp
// For each existing non-system team, insert a Lead role definition
migrationBuilder.Sql(@"
    INSERT INTO team_role_definitions (""Id"", ""TeamId"", ""Name"", ""Description"", ""SlotCount"", ""Priorities"", ""SortOrder"", ""CreatedAt"", ""UpdatedAt"")
    SELECT gen_random_uuid(), t.""Id"", 'Lead', 'Team leadership role', 1, 'Critical', 0,
           NOW() AT TIME ZONE 'UTC', NOW() AT TIME ZONE 'UTC'
    FROM teams t
    WHERE t.""SystemTeamType"" = 'None'
    AND NOT EXISTS (
        SELECT 1 FROM team_role_definitions d WHERE d.""TeamId"" = t.""Id"" AND lower(d.""Name"") = 'lead'
    )
");
```

- [ ] **Step 2: Apply migration**

Run: `dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web`

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat: seed Lead role definitions for existing teams"
```
