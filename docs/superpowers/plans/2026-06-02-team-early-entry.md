# Team Early Entry Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

## As-built amendments (post-design)

This plan describes an earlier shape (and an interim art-specific/single-team console). The shipped feature is generic and per-team (original plan preserved below as history):

- **Early Entry is a generic per-team capability.** The `Team.EarlyEntryEnabled` checkbox ("Enable Early Entry") on Edit Team can be set on **multiple teams at once** — not limited to one team, not art-specific.
- **Management surface is a per-team page** at **`Teams/{slug}/EarlyEntry`** (`TeamAdminController.EarlyEntry` + `Views/TeamAdmin/EarlyEntry.cshtml`). There is **no** global admin-shell console — `EarlyEntryAdminController`, its view, and the "Art Early Entry" admin-nav group were deleted. Add form is **human + date + project**; the grants list auto-saves edits on change (no Save button).
- **Authorization is resource-based** via `TeamOperationRequirement.ManageEarlyEntry` (in `TeamAuthorizationHandler`): **coordinators** manage their own team's early entry de facto; a cross-team role **`EETeamAdmin`** manages any EE-enabled team; `TeamsAdmin`/`Board`/`Admin` manage any team. `EETeamAdmin` is in `RoleNames.All` and `RoleNames.BoardManageableRoles` but **not** `AnyAdminRole` (its surface is the team Details page, not the admin shell). The Team Details "Team Management" card surfaces an **Early Entry** link when EE is enabled and the viewer can manage it.
- **Source label is team-derived:** `"{TeamName}: {ProjectName}"` (mirroring Shifts), not `"Art: …"`; the projection requires the grant's `Team` nav to be loaded.
- **Read model:** the service returns a `TeamEarlyEntryGrantInfo` projection (not the EF entity), per the service-entity-boundary ratchet.
- **Admin flag binding:** the Edit Team `EarlyEntryEnabled` checkbox follows the page's own gate (TeamsAdmin/Board/Admin); the prior admin-only suppression hack was removed. `AddEarlyEntryGrantAsync` rejects an empty `UserId`.
- **Migration:** sentinel-safe bool (`IsRequired()`, not `HasDefaultValue(false)`).
- **GDPR export + right-to-erasure + user-merge** are covered as designed.
- **Known follow-up:** the same admin-only-flag-suppression footgun affects `IsSensitive` (pre-existing) — tracked as **nobodies-collective/Humans#824**. The admin dashboard "Recent activity" panel was gated to `AdminOnly` as part of the security review.

**Goal:** Make Teams a third `IEarlyEntryProvider` so the Creativity department (or any team an admin enables) can grant early entry (human + date + art-project name), surfaced to the existing EE roster/ticket machinery as `"Art: {project}"`.

**Architecture:** A new Teams-owned table `team_early_entry_grants` (one row = one human's EE grant for one art project), an admin-only `Team.EarlyEntryEnabled` flag, and `TeamService` implementing `IEarlyEntryProvider`. Management is gated by a **dedicated individually-granted role `EarlyEntryArtAdmin`** (cantina-style — granted via the existing role-assignment flow, used by exactly one policy), **not** team-coordinator status. Strict layering (Controller → Service → Repository → DbContext); cross-section calls only via service interfaces. GDPR export + right-to-erasure + user-merge all covered.

**Tech Stack:** .NET, EF Core (Npgsql), NodaTime (`LocalDate`/`Instant`), Clean Architecture (Domain/Application/Infrastructure/Web), xUnit (`[HumansFact]`), Moq for pure-logic tests.

**Spec:** `docs/superpowers/specs/2026-06-02-team-early-entry-design.md`

**Branch/worktree:** Implement in a worktree `.worktrees/team-early-entry` off `feat/team-early-entry` (per repo git workflow). Build/test with `-v quiet` (`dotnet build Humans.slnx -v quiet`, `dotnet test Humans.slnx -v quiet`).

---

## File Structure

**Create**
- `src/Humans.Domain/Entities/TeamEarlyEntryGrant.cs` — the grant entity.
- `src/Humans.Infrastructure/Data/Configurations/Teams/TeamEarlyEntryGrantConfiguration.cs` — EF mapping.
- `src/Humans.Application/Services/Teams/TeamEarlyEntryProjection.cs` — pure grant → `EarlyEntryGrant` projection.
- `src/Humans.Web/Models/TeamEarlyEntryViewModels.cs` — management page view model(s).
- `src/Humans.Web/Views/TeamAdmin/EarlyEntry.cshtml` — management page.
- `tests/Humans.Application.Tests/.../TeamEarlyEntryProjectionTests.cs`
- `tests/Humans.Application.Tests/.../TeamServiceEarlyEntryTests.cs`
- EF migration under `src/Humans.Infrastructure/Migrations/` (generated).

**Modify**
- `src/Humans.Domain/Entities/Team.cs` — add `EarlyEntryEnabled`.
- `src/Humans.Infrastructure/Data/Configurations/Teams/TeamConfiguration.cs` — store default for the flag.
- `src/Humans.Infrastructure/Data/HumansDbContext.cs` — add `DbSet<TeamEarlyEntryGrant>`.
- `src/Humans.Application/Interfaces/Repositories/ITeamRepository.cs` — grant methods.
- `src/Humans.Infrastructure/Repositories/Teams/TeamRepository.cs` — implementations.
- `src/Humans.Application/Interfaces/Teams/ITeamService.cs` — management methods + `earlyEntryEnabled` on `UpdateTeamAsync`.
- `src/Humans.Application/Services/Teams/TeamService.cs` — provider, CRUD, merge, GDPR export, erasure, new `IEarlyEntryInvalidator` dep.
- `src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs` — pass-throughs.
- `src/Humans.Web/Extensions/Sections/TeamsSectionExtensions.cs` — register `IEarlyEntryProvider`.
- `src/Humans.Application/Interfaces/Gdpr/GdprExportSections.cs` — new section constant.
- `src/Humans.Application/Services/Users/AccountLifecycle/AccountDeletionService.cs` — erasure call.
- `src/Humans.Web/Models/TeamViewModels.cs` — `EarlyEntryEnabled` on edit + detail VMs.
- `src/Humans.Web/Views/Team/EditTeam.cshtml` — checkbox.
- `src/Humans.Web/Views/Team/Details.cshtml` — management-card link.
- `src/Humans.Web/Controllers/TeamController.cs` — edit GET/POST flag wiring + `CanManageEarlyEntry` on the detail VM.
- `src/Humans.Web/Controllers/TeamAdminController.cs` — management actions (role-gated).
- `src/Humans.Application/Interfaces/Teams/ITeamPageService.cs` — add `EarlyEntryEnabled` to `TeamPageTeamSummary`.
- `src/Humans.Application/Services/Teams/TeamPageSummaryMapper.cs` — carry `EarlyEntryEnabled` through `Map`.
- `src/Humans.Domain/Enums/AuditAction.cs` — three new audit actions.
- `src/Humans.Domain/Constants/RoleNames.cs` — `EarlyEntryArtAdmin` role + add to `BoardManageableRoles`.
- `src/Humans.Web/Authorization/PolicyNames.cs` — `EarlyEntryArtAdminOrAdmin`.
- `src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs` — register the policy.

**Reuse-first note:** the 9 new repo methods + 5 new service methods are all genuinely needed (CRUD + provider filter + GDPR export/erasure + merge). No existing method covers them; the table is new and only `TeamRepository` may touch it. Auth reuses the **existing role-assignment system** (no new grant/revoke UI or table — `EarlyEntryArtAdmin` is granted via `ProfileController.AddRole` exactly like `CantinaAdmin`); the human picker, audit log, and EE invalidator are reused, not recreated.

---

## Chunk 1: Data layer (entity, flag, EF config, migration)

### Task 1: `TeamEarlyEntryGrant` entity + `Team.EarlyEntryEnabled`

**Files:**
- Create: `src/Humans.Domain/Entities/TeamEarlyEntryGrant.cs`
- Modify: `src/Humans.Domain/Entities/Team.cs`

- [ ] **Step 1: Create the entity**

```csharp
// src/Humans.Domain/Entities/TeamEarlyEntryGrant.cs
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// One early-entry grant owned by the Teams section: a human (<see cref="UserId"/>)
/// may enter on <see cref="EntryDate"/> for a named art project. Surfaced to the
/// cross-section EE roster as "Art: {ProjectName}". No EF navigation to User — the
/// bare FK is resolved through IUserServiceRead at the service layer.
/// </summary>
public sealed class TeamEarlyEntryGrant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TeamId { get; init; }
    public Team Team { get; init; } = null!;   // FK nav to owning team only (same-section)
    public Guid UserId { get; init; }
    public LocalDate EntryDate { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public Instant CreatedAt { get; init; }
    public Guid CreatedByUserId { get; init; }
    public Instant? UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Add the flag to `Team`**

In `src/Humans.Domain/Entities/Team.cs`, alongside the other boolean flags (`IsSensitive`, `HasBudget`, `IsHidden`), add:

```csharp
/// <summary>Admin-only gate: when true this team contributes early-entry grants
/// (see <see cref="TeamEarlyEntryGrant"/>) and exposes the EE management page to
/// humans holding the EarlyEntryArtAdmin role. Default false. Toggling it never
/// deletes existing grants.</summary>
public bool EarlyEntryEnabled { get; set; }
```

Also add the inverse navigation collection near `Members` (getter-only, matching the existing `Members` collection style):

```csharp
public ICollection<TeamEarlyEntryGrant> EarlyEntryGrants { get; } = new List<TeamEarlyEntryGrant>();
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Humans.Domain -v quiet`
Expected: builds clean.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Entities/TeamEarlyEntryGrant.cs src/Humans.Domain/Entities/Team.cs
git commit -m "feat(teams): add TeamEarlyEntryGrant entity + EarlyEntryEnabled flag"
```

### Task 2: EF configuration + DbSet + flag default

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/Teams/TeamEarlyEntryGrantConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Teams/TeamConfiguration.cs`

- [ ] **Step 1: Configure the table**

```csharp
// src/Humans.Infrastructure/Data/Configurations/Teams/TeamEarlyEntryGrantConfiguration.cs
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Teams;

public sealed class TeamEarlyEntryGrantConfiguration : IEntityTypeConfiguration<TeamEarlyEntryGrant>
{
    public void Configure(EntityTypeBuilder<TeamEarlyEntryGrant> builder)
    {
        builder.ToTable("team_early_entry_grants");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.ProjectName)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(g => g.EntryDate).IsRequired();   // LocalDate via Npgsql NodaTime
        builder.Property(g => g.CreatedAt).IsRequired();
        builder.Property(g => g.UpdatedAt);                // nullable Instant

        // Same-section FK to Team only — grants die with the team.
        builder.HasOne(g => g.Team)
            .WithMany(t => t.EarlyEntryGrants)
            .HasForeignKey(g => g.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        // NO navigation to User (cross-section). Bare Guid resolved via IUserServiceRead.
        builder.HasIndex(g => g.TeamId);
        builder.HasIndex(g => g.UserId);
    }
}
```

- [ ] **Step 2: Register the DbSet**

In `src/Humans.Infrastructure/Data/HumansDbContext.cs`, near the other Teams DbSets:

```csharp
public DbSet<TeamEarlyEntryGrant> TeamEarlyEntryGrants => Set<TeamEarlyEntryGrant>();
```

- [ ] **Step 3: Store default for the flag**

In `TeamConfiguration.cs`, alongside the existing flags. ⚠️ **Do NOT use `HasDefaultValue(false)`** — on a bool that triggers EF's "sentinel trap": EF treats CLR `false` as "unset" and emits empty `UPDATE teams SET WHERE …` rows for the seeded system teams, which is invalid SQL on Postgres and crashes the migration. For a **default-false** bool just mark it required (the CLR default backfills existing rows; the generated `AddColumn` still gets `defaultValue: false` for the backfill):

```csharp
builder.Property(t => t.EarlyEntryEnabled).IsRequired();
```

(The `true`-defaulting flags in this file use `.HasDefaultValue(true).HasSentinel(true)`; a false default needs neither.)

- [ ] **Step 4: Build**

Run: `dotnet build src/Humans.Infrastructure -v quiet`
Expected: builds clean. (If a configuration auto-discovery test exists, the new config must be picked up by `ApplyConfigurationsFromAssembly` — it is, since that's how the others register.)

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Data/
git commit -m "feat(teams): EF config + DbSet for team_early_entry_grants"
```

### Task 3: EF migration

**Files:**
- Create: migration under `src/Humans.Infrastructure/Migrations/`

> NodaTime/Npgsql maps `LocalDate` → `date` and `Instant` → `timestamptz`. Do not hand-edit unless the reviewer flags it.

- [ ] **Step 1: Generate the migration**

Run:
```bash
dotnet ef migrations add AddTeamEarlyEntry \
  --project src/Humans.Infrastructure \
  --startup-project src/Humans.Web
```
Expected: a migration adding `team_early_entry_grants` (PK, `TeamId` FK cascade, `UserId`, `EntryDate date`, `ProjectName varchar(256)`, `CreatedAt`/`UpdatedAt timestamptz`, `CreatedByUserId`, indexes on TeamId+UserId) **and** the `EarlyEntryEnabled boolean not null default false` column on `teams`.

- [ ] **Step 2: Inspect the migration**

Read the generated `*_AddTeamEarlyEntry.cs`. Confirm: one `CreateTable`, one `AddColumn` on `teams`, FK `OnDelete: Cascade`, both indexes, no unintended drops/renames.

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: builds clean.

- [ ] **Step 4: MANDATORY GATE — EF migration review**

Per repo doctrine, run the EF migration-review pass on this migration (`.claude/agents/ef-migration-reviewer.md` / the migration-review gate). Fix anything it flags. If the migration is mid-chain or messy, use the `/ef-regen` skill.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat(teams): migration AddTeamEarlyEntry (grants table + flag)"
```

---

## Chunk 2: Repository

### Task 4: `ITeamRepository` grant methods

**Files:**
- Modify: `src/Humans.Application/Interfaces/Repositories/ITeamRepository.cs`

- [ ] **Step 1: Add method signatures** (in a new `// Early-entry grants` region)

```csharp
// Early-entry grants (team_early_entry_grants)
Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForEnabledTeamsAsync(CancellationToken ct = default);
Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default);
Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default);
Task<TeamEarlyEntryGrant?> FindEarlyEntryGrantForMutationAsync(Guid grantId, CancellationToken ct = default);
Task AddEarlyEntryGrantAsync(TeamEarlyEntryGrant grant, CancellationToken ct = default);
Task UpdateEarlyEntryGrantAsync(TeamEarlyEntryGrant grant, CancellationToken ct = default);
Task RemoveEarlyEntryGrantAsync(Guid grantId, CancellationToken ct = default);
Task ReassignEarlyEntryGrantsAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);
Task RemoveEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default);
```

Add `using Humans.Domain.Entities;` if not already imported.

- [ ] **Step 2: Build** — `dotnet build src/Humans.Application -v quiet`. Expected: FAILS (TeamRepository doesn't implement the new members yet). That's the failing state Task 5 fixes.

### Task 5: `TeamRepository` implementations

**Files:**
- Modify: `src/Humans.Infrastructure/Repositories/Teams/TeamRepository.cs`

- [ ] **Step 1: Implement** (each opens its own `DbContext` via `factory`, matching the existing style)

```csharp
// ==========================================================================
// Early-entry grants
// ==========================================================================

public async Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForEnabledTeamsAsync(CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.TeamEarlyEntryGrants
        .AsNoTracking()
        .Where(g => g.Team.EarlyEntryEnabled)
        .ToListAsync(ct);
}

public async Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.TeamEarlyEntryGrants
        .AsNoTracking()
        .Where(g => g.TeamId == teamId)
        .OrderBy(g => g.ProjectName).ThenBy(g => g.EntryDate)
        .ToListAsync(ct);
}

public async Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.TeamEarlyEntryGrants
        .AsNoTracking()
        .Where(g => g.UserId == userId)
        .ToListAsync(ct);
}

public async Task<TeamEarlyEntryGrant?> FindEarlyEntryGrantForMutationAsync(Guid grantId, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.TeamEarlyEntryGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct);
}

public async Task AddEarlyEntryGrantAsync(TeamEarlyEntryGrant grant, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    db.TeamEarlyEntryGrants.Add(grant);
    await db.SaveChangesAsync(ct);
}

public async Task UpdateEarlyEntryGrantAsync(TeamEarlyEntryGrant grant, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    db.TeamEarlyEntryGrants.Update(grant);
    await db.SaveChangesAsync(ct);
}

public async Task RemoveEarlyEntryGrantAsync(Guid grantId, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    await db.TeamEarlyEntryGrants.Where(g => g.Id == grantId).ExecuteDeleteAsync(ct);
}

public async Task ReassignEarlyEntryGrantsAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    await db.TeamEarlyEntryGrants
        .Where(g => g.UserId == sourceUserId)
        .ExecuteUpdateAsync(s => s.SetProperty(g => g.UserId, targetUserId), ct);
}

public async Task RemoveEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);
    await db.TeamEarlyEntryGrants.Where(g => g.UserId == userId).ExecuteDeleteAsync(ct);
}
```

> Note `UpdateEarlyEntryGrantAsync` takes a detached entity from the service (which set `EntryDate`/`ProjectName`/`UpdatedAt`). `Update` marks all columns modified — fine at this scale. Alternatively load-tracked-then-save; either is acceptable.

- [ ] **Step 2: Build** — `dotnet build src/Humans.Infrastructure -v quiet`. Expected: builds clean.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Interfaces/Repositories/ITeamRepository.cs \
        src/Humans.Infrastructure/Repositories/Teams/TeamRepository.cs
git commit -m "feat(teams): repository methods for early-entry grants"
```

---

## Chunk 3: Projection, service, provider, DI, GDPR, merge

### Task 6: `TeamEarlyEntryProjection` (TDD)

**Files:**
- Create: `src/Humans.Application/Services/Teams/TeamEarlyEntryProjection.cs`
- Test: `tests/Humans.Application.Tests/Services/Teams/TeamEarlyEntryProjectionTests.cs` (mirror the directory of existing Teams tests)

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Humans.Application.Services.Teams;
using Humans.Domain.Entities;
using Humans.TestSupport; // [HumansFact] — match the attribute's namespace used elsewhere
using NodaTime;

namespace Humans.Application.Tests.Services.Teams;

public class TeamEarlyEntryProjectionTests
{
    [HumansFact]
    public void Project_maps_each_grant_to_Art_prefixed_source_and_preserves_date()
    {
        var u1 = Guid.NewGuid();
        var grants = new List<TeamEarlyEntryGrant>
        {
            new() { UserId = u1, EntryDate = new LocalDate(2026, 7, 3), ProjectName = "Flame Tower" },
        };

        var result = TeamEarlyEntryProjection.Project(grants);

        result.Should().ContainSingle();
        result[0].UserId.Should().Be(u1);
        result[0].EntryDate.Should().Be(new LocalDate(2026, 7, 3));
        result[0].Source.Should().Be("Art: Flame Tower");
    }

    [HumansFact]
    public void Project_empty_input_returns_empty()
        => TeamEarlyEntryProjection.Project([]).Should().BeEmpty();
}
```

- [ ] **Step 2: Run, expect FAIL** — `dotnet test Humans.slnx -v quiet --filter TeamEarlyEntryProjectionTests`. Expected: compile error / fail (class missing).

- [ ] **Step 3: Implement**

```csharp
// src/Humans.Application/Services/Teams/TeamEarlyEntryProjection.cs
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Teams;

/// <summary>
/// Pure projection from Teams' early-entry grants to EE grants. Per-grant date;
/// source label is always "Art: {ProjectName}". Kept as a small pure, unit-tested
/// helper (Camps/Shifts now inline their equivalent projection in the provider).
/// </summary>
internal static class TeamEarlyEntryProjection
{
    internal static IReadOnlyList<EarlyEntryGrant> Project(IReadOnlyList<TeamEarlyEntryGrant> grants)
    {
        var result = new List<EarlyEntryGrant>(grants.Count);
        foreach (var g in grants)
            result.Add(new EarlyEntryGrant(g.UserId, g.EntryDate, $"Art: {g.ProjectName}"));
        return result;
    }
}
```

- [ ] **Step 4: Run, expect PASS.** Then commit:

```bash
git add src/Humans.Application/Services/Teams/TeamEarlyEntryProjection.cs \
        tests/Humans.Application.Tests/Services/Teams/TeamEarlyEntryProjectionTests.cs
git commit -m "feat(teams): TeamEarlyEntryProjection (Art: {project})"
```

### Task 7: Audit actions + GDPR section constant

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs`
- Modify: `src/Humans.Application/Interfaces/Gdpr/GdprExportSections.cs`

- [ ] **Step 1:** Add three values to `AuditAction`. The enum is **positional/append-only** (it carries a "Do not remove — audit enum is positional" comment) — **append at the very end of the enum, never insert mid-list**:

```csharp
EarlyEntryGranted,
EarlyEntryUpdated,
EarlyEntryRevoked,
```

- [ ] **Step 2:** Add a section constant to `GdprExportSections` (match the existing string-constant style, e.g. `TeamMemberships`):

```csharp
public const string TeamEarlyEntry = "TeamEarlyEntry";
```

- [ ] **Step 3: Build** — `dotnet build src/Humans.Domain src/Humans.Application -v quiet`. Commit:

```bash
git add src/Humans.Domain/Enums/AuditAction.cs src/Humans.Application/Interfaces/Gdpr/GdprExportSections.cs
git commit -m "feat(teams): audit actions + GDPR section for early entry"
```

### Task 8: `ITeamService` surface (management + provider + flag param)

**Files:**
- Modify: `src/Humans.Application/Interfaces/Teams/ITeamService.cs`

- [ ] **Step 1: Add management method signatures** (Teams-internal — NOT on `ITeamServiceRead`):

```csharp
// Early-entry management (Teams-internal; called by TeamAdminController)
Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default);
Task AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default);
Task EditEarlyEntryGrantAsync(Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default);
Task RemoveEarlyEntryGrantAsync(Guid grantId, Guid actorUserId, CancellationToken ct = default);
Task DeleteEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default); // right-to-erasure
```

Add `using Humans.Domain.Entities;` and `using NodaTime;` if needed.

- [ ] **Step 1b: Add `EarlyEntryEnabled` to the `TeamInfo` read model.** `TeamInfo` (the cross-section projection returned by `ResolveTeamManagementAsync`, the detail VM builder, etc.) is defined in this same file (`ITeamService.cs`). Add a `bool EarlyEntryEnabled` member to the record so the Web layer can read the flag without re-fetching the `Team` entity:

```csharp
// in the TeamInfo record definition — add a parameter/property:
bool EarlyEntryEnabled
```

Match the record's existing style (positional record params vs. init properties). This is the single source the controller guard (`if (!team.EarlyEntryEnabled)`) and the Team detail VM both read. It is populated in `CachingTeamService.BuildTeamInfo` (Task 10, Step 0).

- [ ] **Step 2: Add `earlyEntryEnabled` to `UpdateTeamAsync`** — append an optional param (matching the existing `bool?` optional-flag pattern) to the interface signature:

```csharp
        bool? isPromotedToDirectory = null,
        bool? earlyEntryEnabled = null,   // <-- add this line before the CancellationToken
        CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Build** — expect FAIL (TeamService + CachingTeamService don't implement yet). Tasks 9–10 fix.

### Task 9: `TeamService` — provider, CRUD, merge, GDPR, erasure (TDD)

**Files:**
- Modify: `src/Humans.Application/Services/Teams/TeamService.cs`
- Test: `tests/Humans.Application.Tests/Services/Teams/TeamServiceEarlyEntryTests.cs`

- [ ] **Step 1: Add the `IEarlyEntryInvalidator` dependency + provider interface**

Update the class declaration (add `IEarlyEntryProvider`) and the primary constructor (add the invalidator):

```csharp
public sealed class TeamService(
    ITeamRepository repo,
    IAuditLogService auditLogService,
    INotificationEmitter notificationService,
    IShiftManagementService shiftManagementService,
    INotificationMeterCacheInvalidator notificationMeterInvalidator,
    IShiftAuthorizationInvalidator shiftAuthInvalidator,
    IAdminAuthorizationService adminAuthorization,
    IEarlyEntryInvalidator earlyEntryInvalidator,   // <-- new
    IServiceProvider serviceProvider,
    IClock clock,
    ILogger<TeamService> logger)
    : ITeamService, IGoogleGroupMembershipSource, IUserDataContributor, IUserMerge, IEarlyEntryProvider // <-- + provider
```

Add `using Humans.Application.Interfaces.EarlyEntry;`.

- [ ] **Step 2: Write the failing tests** (pure-logic; mock `ITeamRepository`, `IEarlyEntryInvalidator`, `IAuditLogService`, `IClock`)

```csharp
// tests/.../TeamServiceEarlyEntryTests.cs — key cases:
// 1. GetEarlyEntriesAsync returns only grants from EE-enabled teams (repo already filters),
//    mapped to "Art: {project}".
// 2. GetEarlyEntriesAsync returns empty when repo returns empty.
// 3. AddEarlyEntryGrantAsync: team enabled -> repo.AddEarlyEntryGrantAsync called with
//    correct fields (CreatedAt from clock, CreatedByUserId = actor), invalidator.InvalidateUser(userId),
//    audit EarlyEntryGranted.
// 4. AddEarlyEntryGrantAsync: team NOT enabled -> throws/no-op (assert repo.Add never called).
// 5. EditEarlyEntryGrantAsync: loads via FindEarlyEntryGrantForMutationAsync, updates date+project+UpdatedAt,
//    invalidates that user, audit EarlyEntryUpdated.
// 6. RemoveEarlyEntryGrantAsync: loads grant, deletes, invalidates the grant's user, audit EarlyEntryRevoked.
// 7. DeleteEarlyEntryGrantsForUserAsync: fetches user's grants, removes all, invalidates user.
// 8. ReassignAsync (merge): also calls repo.ReassignEarlyEntryGrantsAsync(source,target) and
//    invalidates both users.
// 9. ContributeForUserAsync (GDPR export): includes a TeamEarlyEntry slice with the user's grants.
```

Write each as a `[HumansFact]` using Moq, asserting the repo/invalidator/audit interactions. (Follow the existing `TeamService` test file(s) for harness/mock setup conventions.)

- [ ] **Step 3: Run, expect FAIL** — `dotnet test Humans.slnx -v quiet --filter TeamServiceEarlyEntryTests`.

- [ ] **Step 4: Implement the methods on `TeamService`**

```csharp
// ---- IEarlyEntryProvider ----
public async Task<IReadOnlyList<EarlyEntryGrant>> GetEarlyEntriesAsync(CancellationToken ct)
{
    var grants = await repo.GetEarlyEntryGrantsForEnabledTeamsAsync(ct);
    return TeamEarlyEntryProjection.Project(grants);
}

// ---- Early-entry management ----
public Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default)
    => repo.GetEarlyEntryGrantsForTeamAsync(teamId, ct);

public async Task AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
{
    var team = await repo.GetByIdAsync(teamId, ct)
        ?? throw new InvalidOperationException($"Team {teamId} not found.");
    if (!team.EarlyEntryEnabled)
        throw new InvalidOperationException($"Early entry is not enabled for team {teamId}.");

    var grant = new TeamEarlyEntryGrant
    {
        TeamId = teamId,
        UserId = userId,
        EntryDate = entryDate,
        ProjectName = projectName.Trim(),
        CreatedAt = clock.GetCurrentInstant(),
        CreatedByUserId = actorUserId,
    };
    await repo.AddEarlyEntryGrantAsync(grant, ct);
    earlyEntryInvalidator.InvalidateUser(userId);
    await auditLogService.LogAsync(AuditAction.EarlyEntryGranted, nameof(TeamEarlyEntryGrant), grant.Id,
        $"EE granted to {userId} on {entryDate} for \"{grant.ProjectName}\" (team {teamId})", actorUserId);
}

public async Task EditEarlyEntryGrantAsync(Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
{
    var grant = await repo.FindEarlyEntryGrantForMutationAsync(grantId, ct)
        ?? throw new InvalidOperationException($"EE grant {grantId} not found.");
    grant.EntryDate = entryDate;
    grant.ProjectName = projectName.Trim();
    grant.UpdatedAt = clock.GetCurrentInstant();
    await repo.UpdateEarlyEntryGrantAsync(grant, ct);
    earlyEntryInvalidator.InvalidateUser(grant.UserId);
    await auditLogService.LogAsync(AuditAction.EarlyEntryUpdated, nameof(TeamEarlyEntryGrant), grant.Id,
        $"EE updated for {grant.UserId}: {entryDate}, \"{grant.ProjectName}\"", actorUserId);
}

public async Task RemoveEarlyEntryGrantAsync(Guid grantId, Guid actorUserId, CancellationToken ct = default)
{
    var grant = await repo.FindEarlyEntryGrantForMutationAsync(grantId, ct);
    if (grant is null) return; // idempotent
    await repo.RemoveEarlyEntryGrantAsync(grantId, ct);
    earlyEntryInvalidator.InvalidateUser(grant.UserId);
    await auditLogService.LogAsync(AuditAction.EarlyEntryRevoked, nameof(TeamEarlyEntryGrant), grantId,
        $"EE revoked for {grant.UserId} (team {grant.TeamId})", actorUserId);
}

public async Task DeleteEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default)
{
    var grants = await repo.GetEarlyEntryGrantsForUserAsync(userId, ct);
    if (grants.Count == 0) return;
    await repo.RemoveEarlyEntryGrantsForUserAsync(userId, ct);
    earlyEntryInvalidator.InvalidateUser(userId);
}
```

- [ ] **Step 5: Extend `ReassignAsync` (merge)** — at the end of the existing method body, before/after the join-request fold, add:

```csharp
// Early-entry grants fold.
await repo.ReassignEarlyEntryGrantsAsync(sourceUserId, targetUserId, cancellationToken);
earlyEntryInvalidator.InvalidateUser(sourceUserId);
earlyEntryInvalidator.InvalidateUser(targetUserId);
```

- [ ] **Step 6: Extend `ContributeForUserAsync` (GDPR export)** — `ContributeForUserAsync` already builds a `teamIds` list and a `teamsById` dict via one `GetByIdsWithParentsAsync`, plus a `GetTeamName(Guid)` local. **Reuse them**: fetch the user's EE grants, fold their team ids into the existing `teamIds` *before* the `GetByIdsWithParentsAsync` call (so it stays a single lookup), then append the slice using the existing `GetTeamName` helper:

```csharp
// near the top, alongside memberships/joinRequests:
var eeGrants = await repo.GetEarlyEntryGrantsForUserAsync(userId, ct);

// fold EE team ids into the existing teamIds list so the single GetByIdsWithParentsAsync covers them:
var teamIds = memberships.Select(tm => tm.TeamId)
    .Concat(joinRequests.Select(tjr => tjr.TeamId))
    .Concat(eeGrants.Select(g => g.TeamId))          // <-- add
    .Distinct()
    .ToList();
// ... existing teamsById = await repo.GetByIdsWithParentsAsync(teamIds, ct); and GetTeamName(...) unchanged ...

var earlyEntrySlice = new UserDataSlice(GdprExportSections.TeamEarlyEntry, eeGrants.Select(g => new
{
    TeamName = GetTeamName(g.TeamId),
    g.ProjectName,
    EntryDate = g.EntryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    GrantedAt = g.CreatedAt.ToInvariantInstantString(),
}).ToList());

return [membershipSlice, joinRequestSlice, earlyEntrySlice];
```

- [ ] **Step 7: Add the `earlyEntryEnabled` flag handling to `UpdateTeamAsync`** — in the existing method, after applying it to the loaded `Team` (alongside `hasBudget`/`isHidden`/`isSensitive`), invalidate EE if it toggled:

```csharp
if (earlyEntryEnabled is { } eeFlag && eeFlag != team.EarlyEntryEnabled)
{
    team.EarlyEntryEnabled = eeFlag;
    earlyEntryInvalidator.InvalidateAll(); // flag flip changes who contributes; cheap at ~500 users
}
```

(Match the method's existing optional-`bool?` apply style; add `bool? earlyEntryEnabled = null` to the impl signature to mirror the interface.)

- [ ] **Step 8: Run tests, expect PASS.** `dotnet test Humans.slnx -v quiet --filter TeamServiceEarlyEntryTests`.

- [ ] **Step 9: Commit**

```bash
git add src/Humans.Application/Services/Teams/TeamService.cs \
        src/Humans.Application/Interfaces/Teams/ITeamService.cs \
        tests/Humans.Application.Tests/Services/Teams/TeamServiceEarlyEntryTests.cs
git commit -m "feat(teams): TeamService EE provider, CRUD, merge, GDPR export"
```

### Task 10: `CachingTeamService` pass-throughs

**Files:**
- Modify: `src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs`

- [ ] **Step 0: Populate `EarlyEntryEnabled` in `BuildTeamInfo`.** Find the `BuildTeamInfo` method (it constructs each `TeamInfo` from a `Team` entity during cache warm/build) and set the new member:

```csharp
EarlyEntryEnabled = team.EarlyEntryEnabled,   // match BuildTeamInfo's construction style
```

This is what makes the flag visible to the controller guard and the Team detail VM.

- [ ] **Step 1: Implement the new `ITeamService` members as pass-throughs** (these do not touch the `TeamInfo` cache; the inner service owns EE invalidation). `CachingTeamService` already has both a `Func<ITeamService, Task<TResult>>` `WithInner` and a void-returning `WithInner(Func<ITeamService, Task>)` overload (~line 1084) — use the existing `WithInner` for both:

```csharp
public Task<IReadOnlyList<TeamEarlyEntryGrant>> GetEarlyEntryGrantsForTeamAsync(Guid teamId, CancellationToken ct = default)
    => WithInner(inner => inner.GetEarlyEntryGrantsForTeamAsync(teamId, ct));

public Task AddEarlyEntryGrantAsync(Guid teamId, Guid userId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
    => WithInner(inner => inner.AddEarlyEntryGrantAsync(teamId, userId, entryDate, projectName, actorUserId, ct));

public Task EditEarlyEntryGrantAsync(Guid grantId, LocalDate entryDate, string projectName, Guid actorUserId, CancellationToken ct = default)
    => WithInner(inner => inner.EditEarlyEntryGrantAsync(grantId, entryDate, projectName, actorUserId, ct));

public Task RemoveEarlyEntryGrantAsync(Guid grantId, Guid actorUserId, CancellationToken ct = default)
    => WithInner(inner => inner.RemoveEarlyEntryGrantAsync(grantId, actorUserId, ct));

public Task DeleteEarlyEntryGrantsForUserAsync(Guid userId, CancellationToken ct = default)
    => WithInner(inner => inner.DeleteEarlyEntryGrantsForUserAsync(userId, ct));
```

- [ ] **Step 2: Add `earlyEntryEnabled` to the `UpdateTeamAsync` pass-through** signature and forward it:

```csharp
        bool? isPromotedToDirectory = null,
        bool? earlyEntryEnabled = null,
        CancellationToken cancellationToken = default)
    {
        var result = await WithInner(inner => inner.UpdateTeamAsync(
            teamId, name, description, requiresApproval, isActive, parentTeamId,
            googleGroupPrefix, customSlug, hasBudget, isHidden, isSensitive,
            isPromotedToDirectory, earlyEntryEnabled, cancellationToken));
        InvalidateTeamsCache();
        return result;
    }
```

- [ ] **Step 3: Build** — `dotnet build Humans.slnx -v quiet`. Expected: clean.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs
git commit -m "feat(teams): CachingTeamService pass-throughs for early entry"
```

### Task 11: DI registration

**Files:**
- Modify: `src/Humans.Web/Extensions/Sections/TeamsSectionExtensions.cs`

- [ ] **Step 1:** After the inner-service registrations (next to the `IUserDataContributor` line at ~line 28), add:

```csharp
services.AddScoped<IEarlyEntryProvider>(sp => sp.GetRequiredService<TeamsTeamService>());
```

Add `using Humans.Application.Interfaces.EarlyEntry;` (the `TeamsTeamService` alias already exists at the top of the file).

> **Why the scoped inner, not the singleton `CachingTeamService`:** this mirrors **Shifts** (`VolunteerTrackingExportService` is registered as a scoped `IEarlyEntryProvider`). Camps instead registers its caching decorator (`CachingCampService`) as the provider — but only because its EE read is served from the cached `CampInfo`. Teams' grants are **not** in the `TeamInfo` cache, so there's no caching benefit; the inner scoped `TeamService` reading the repo directly is correct, and `CachingTeamService` does **not** implement `IEarlyEntryProvider`. The EE orchestrator (`EarlyEntryService`) is keyed-scoped, so it resolves a scoped provider fine (same as Shifts today).

- [ ] **Step 2: Build** — `dotnet build Humans.slnx -v quiet`. Expected: clean.

- [ ] **Step 3: Verify the provider is wired** — add/confirm a `[HumansFact]` (or reuse an existing DI-resolution test) asserting that resolving `IEnumerable<IEarlyEntryProvider>` includes a `TeamService`. If the project has no such test pattern, skip (DI is exercised by app startup + integration).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Extensions/Sections/TeamsSectionExtensions.cs
git commit -m "feat(teams): register TeamService as IEarlyEntryProvider"
```

### Task 12: Wire erasure into `AccountDeletionService`

**Files:**
- Modify: `src/Humans.Application/Services/Users/AccountLifecycle/AccountDeletionService.cs`

- [ ] **Step 1:** In `AnonymizeExpiredAccountAsync`, after the team-membership revoke (step 1) add a step that deletes EE grants:

```csharp
// 1b. Delete team early-entry grants (right-to-erasure; no DB cascade — bare UserId).
await teamService.DeleteEarlyEntryGrantsForUserAsync(userId, ct);
```

(The service already injects `ITeamService teamService`, so no constructor change.)

- [ ] **Step 2:** Add a `[HumansFact]` to the AccountDeletionService test suite asserting `AnonymizeExpiredAccountAsync` calls `teamService.DeleteEarlyEntryGrantsForUserAsync(userId, ...)`. (Mock `ITeamService`; follow the existing test file's setup.)

- [ ] **Step 3: Run** — `dotnet test Humans.slnx -v quiet --filter AccountDeletion`. Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Services/Users/AccountLifecycle/AccountDeletionService.cs tests/
git commit -m "feat(teams): erase early-entry grants on account anonymization"
```

---

## Chunk 4: Web UI

### Task 13: Role + policy (`EarlyEntryArtAdmin`) — cantina-clone

This mirrors how `CantinaAdmin` is wired: a dedicated, **independent** role granted to individuals via the existing role-assignment flow, used by **exactly one** policy that gates only the EE management surface. It is never added to any other policy/role group, so it grants nothing beyond EE management.

**Files:**
- Modify: `src/Humans.Domain/Constants/RoleNames.cs` (new role constant + `BoardManageableRoles`)
- Modify: `src/Humans.Web/Authorization/PolicyNames.cs` (new policy name)
- Modify: `src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs` (register policy)

- [ ] **Step 1:** In `RoleNames.cs`, add the role constant next to `CantinaAdmin` (match the existing `public const string` style):

```csharp
public const string EarlyEntryArtAdmin = "EarlyEntryArtAdmin";
```

- [ ] **Step 1b: Add it to `RoleNames.All`** (the list ~lines 97–113). This is **test-enforced**: `RoleNamesTests.All_ContainsEveryDefinedRoleConstant` (tests/Humans.Domain.Tests/Constants/RoleNamesTests.cs) reflects over every `public const string` and asserts `All` contains it — omitting it fails the build. Add `EarlyEntryArtAdmin` to the `All` collection.

- [ ] **Step 2:** Add `EarlyEntryArtAdmin` to the `BoardManageableRoles` set (the same set that contains `CantinaAdmin`, ~line 131) so Admin/Board/HumanAdmin can grant it via the existing Add-Role flow and it appears in `RoleChecks.GetAssignableRoles`. Do **not** add it to any other grouping (e.g. team-admin role bundles or `AnyAdminRole`) — it must confer nothing beyond EE management.

- [ ] **Step 3:** In `PolicyNames.cs`, add:

```csharp
public const string EarlyEntryArtAdminOrAdmin = nameof(EarlyEntryArtAdminOrAdmin);
```

- [ ] **Step 4:** In `AuthorizationPolicyExtensions.cs`, register the policy next to `CantinaAdminOrAdmin`:

```csharp
options.AddPolicy(PolicyNames.EarlyEntryArtAdminOrAdmin, policy =>
    policy.RequireRole(RoleNames.EarlyEntryArtAdmin, RoleNames.Admin));
```

- [ ] **Step 5: Build** — `dotnet build Humans.slnx -v quiet`. Expected: clean. Commit:

```bash
git add src/Humans.Domain/Constants/RoleNames.cs src/Humans.Web/Authorization/PolicyNames.cs src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
git commit -m "feat(teams): EarlyEntryArtAdmin role + EarlyEntryArtAdminOrAdmin policy"
```

> No new grant/revoke UI or service is needed — `EarlyEntryArtAdmin` is granted through the existing `ProfileController.AddRole` flow exactly like `CantinaAdmin`, and claims are populated by the existing `RoleAssignmentClaimsTransformation`.

### Task 14: Admin flag on Edit Team

**Files:**
- Modify: `src/Humans.Web/Models/TeamViewModels.cs` (`EditTeamViewModel`)
- Modify: `src/Humans.Web/Views/Team/EditTeam.cshtml`
- Modify: `src/Humans.Web/Controllers/TeamController.cs` (`EditTeam` GET + POST)

- [ ] **Step 1:** Add to `EditTeamViewModel`:

```csharp
[Display(Name = "Enable Early Entry")]
public bool EarlyEntryEnabled { get; set; }
```

- [ ] **Step 2:** In `EditTeam.cshtml`, near the other admin-only flag checkboxes (e.g. `IsSensitive`), add a checkbox bound to `EarlyEntryEnabled` with help text like "Lets humans with the Early Entry Art Admin role grant early entry for this team's art projects." Use the same checkbox markup pattern as the sibling flags.

- [ ] **Step 3:** In `TeamController.EditTeam` **GET**, populate `EarlyEntryEnabled = team.EarlyEntryEnabled` when building the VM.

- [ ] **Step 4:** In `TeamController.EditTeam` **POST**, pass `earlyEntryEnabled: model.EarlyEntryEnabled` into the `UpdateTeamAsync(...)` call.

- [ ] **Step 5: Build** — `dotnet build Humans.slnx -v quiet`. Commit:

```bash
git add src/Humans.Web/Models/TeamViewModels.cs src/Humans.Web/Views/Team/EditTeam.cshtml src/Humans.Web/Controllers/TeamController.cs
git commit -m "feat(teams): admin Enable Early Entry checkbox on Edit Team"
```

### Task 15: Management-card link on Team detail

The EE link must be reachable by an `EarlyEntryArtAdmin` who is **not** a team coordinator. The Team Management card currently renders only on `CanCurrentUserManage` (coordinator+admin), so we add a separate `CanManageEarlyEntry` flag and let the card render when **either** is true.

**Files:**
- Modify: `src/Humans.Web/Models/TeamViewModels.cs` (`TeamDetailViewModel`)
- Modify: `src/Humans.Application/Interfaces/Teams/ITeamPageService.cs` (`TeamPageTeamSummary` record)
- Modify: `src/Humans.Application/Services/Teams/TeamPageSummaryMapper.cs` (`Map`)
- Modify: `src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs` (`MapTeamSummary` call site)
- Modify: `src/Humans.Application/Services/Teams/TeamService.cs` (reference-impl `MapTeamSummary` call site)
- Modify: `src/Humans.Web/Controllers/TeamController.cs` (`Details` action — compute the role-based flag from `User`)
- Modify: `src/Humans.Web/Views/Team/Details.cshtml`

- [ ] **Step 1:** Add to `TeamDetailViewModel`:

```csharp
public bool EarlyEntryEnabled { get; set; }
public bool CanManageEarlyEntry { get; set; }
```

- [ ] **Step 2: Plumb `EarlyEntryEnabled` to the detail page.** ⚠️ `TeamController.Details` builds its VM from a **`TeamPageTeamSummary`** (via `TeamPageService.GetTeamPageDetailAsync` → `GetTeamDetailAsync` → `MapTeamSummary` → `TeamPageSummaryMapper.Map`), **not** directly from `TeamInfo`. So route the flag through that chain:
  1. Add `bool EarlyEntryEnabled` to the `TeamPageTeamSummary` record (`ITeamPageService.cs`).
  2. Add an `earlyEntryEnabled` parameter to `TeamPageSummaryMapper.Map(...)` and set it on the constructed record.
  3. Pass `team.EarlyEntryEnabled` (read off the `TeamInfo` — which now carries it from Task 8 Step 1b / Task 10 Step 0) at **both** `Map` call sites: `CachingTeamService.MapTeamSummary` and the `TeamService` reference-impl `MapTeamSummary`.
  4. In `TeamController.Details`, set `vm.EarlyEntryEnabled = teamPage.Team.EarlyEntryEnabled` (match the action's real local-variable name for the page detail / summary).

- [ ] **Step 2b:** Still in `TeamController.Details` (the Web layer, which has `User`), compute the role-based flag after the VM is built:

```csharp
vm.CanManageEarlyEntry = vm.EarlyEntryEnabled &&
    (User.IsInRole(RoleNames.Admin) || User.IsInRole(RoleNames.EarlyEntryArtAdmin));
```

(Keep the claims/role check in the Web layer, not the page service. `RoleNames` is in `Humans.Domain.Constants` — add the `using`.)

- [ ] **Step 3:** In `Details.cshtml`:
  - Where the Team Management card is gated, change the condition so the card renders for EE-admins too: `@if (Model.CanCurrentUserManage || Model.CanManageEarlyEntry)`.
  - Inside the card's `list-group`, add the EE link gated by its own flag:

```cshtml
@if (Model.CanManageEarlyEntry)
{
    <a class="list-group-item list-group-item-action"
       asp-controller="TeamAdmin" asp-action="EarlyEntry" asp-route-slug="@Model.Slug">
        @Localizer["TeamAdmin_EarlyEntry"]
    </a>
}
```

  (Use a localized string key consistent with the other links; add the resource entry if the project requires it. The existing coordinator-only links stay gated on `CanCurrentUserManage` so an EE-admin-only viewer sees just the Early Entry link.)

- [ ] **Step 4: Build** — `dotnet build Humans.slnx -v quiet`. Commit:

```bash
git add src/Humans.Web/Models/TeamViewModels.cs src/Humans.Web/Views/Team/Details.cshtml \
        src/Humans.Web/Controllers/TeamController.cs \
        src/Humans.Application/Interfaces/Teams/ITeamPageService.cs \
        src/Humans.Application/Services/Teams/TeamPageSummaryMapper.cs \
        src/Humans.Application/Services/Teams/TeamService.cs \
        src/Humans.Infrastructure/Services/Teams/CachingTeamService.cs
git commit -m "feat(teams): Early Entry link in Team Management card (role-gated)"
```

### Task 16: Management page (list + add/edit/remove)

**Files:**
- Create: `src/Humans.Web/Models/TeamEarlyEntryViewModels.cs`
- Create: `src/Humans.Web/Views/TeamAdmin/EarlyEntry.cshtml`
- Modify: `src/Humans.Web/Controllers/TeamAdminController.cs`

> **Auth differs from the other `TeamAdminController` actions.** The EE actions are gated by the **role policy** `[Authorize(Policy = PolicyNames.EarlyEntryArtAdminOrAdmin)]` (Task 13), **not** `ResolveTeamManagementAsync` (which checks coordinator-of-team). Mirror `Members` only for the `[Route]`/`[HttpGet]`/`[HttpPost]` attributes + antiforgery. Resolve the team **read-only** by slug (`_teamService.GetTeamBySlugAsync(slug)` → `TeamInfo?`, `NotFound()` if null) and the current user via the base controller's existing current-user helper. Every action adds a defense-in-depth `if (!team.EarlyEntryEnabled) return NotFound();`.

- [ ] **Step 1: View models**

> **`LocalDate` does NOT bind from form POST in this project** — there is no MVC `LocalDate` model binder. The established pattern (see `src/Humans.Web/Models/SetCampSetupForm.cs`) is a **`string` form field** parsed in the controller via `LocalDatePattern.Iso.Parse`. The input models below follow that. Display VMs can hold `LocalDate` directly (no binding needed for rendering).

```csharp
// src/Humans.Web/Models/TeamEarlyEntryViewModels.cs
using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace Humans.Web.Models;

public sealed class TeamEarlyEntryPageViewModel
{
    public Guid TeamId { get; init; }
    public string TeamName { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public IReadOnlyList<TeamEarlyEntryRowViewModel> Grants { get; init; } = [];
}

public sealed class TeamEarlyEntryRowViewModel
{
    public Guid GrantId { get; init; }
    public Guid UserId { get; init; }
    public string HumanName { get; init; } = string.Empty;  // resolved via IUserServiceRead
    public LocalDate EntryDate { get; init; }               // display only — not form-bound
    public string ProjectName { get; init; } = string.Empty;
}

public sealed class AddTeamEarlyEntryInput
{
    [Required] public Guid UserId { get; set; }
    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")] public string EntryDate { get; set; } = string.Empty;
    [Required, StringLength(256)] public string ProjectName { get; set; } = string.Empty;
}

public sealed class EditTeamEarlyEntryInput
{
    [Required] public Guid GrantId { get; set; }
    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")] public string EntryDate { get; set; } = string.Empty;
    [Required, StringLength(256)] public string ProjectName { get; set; } = string.Empty;
}
```

In the controller, parse with the same helper `SetCampSetupForm`/camps controllers use:

```csharp
using NodaTime.Text;
// ...
var parsed = LocalDatePattern.Iso.Parse(input.EntryDate);
if (!parsed.Success)
{
    ModelState.AddModelError(nameof(input.EntryDate), "Enter a date as yyyy-MM-dd.");
    // re-render the page VM (mirror the Members action's invalid-model path)
}
var entryDate = parsed.Value;
```

Use `<input type="date">` in the view (browsers post `yyyy-MM-dd`), bound to the string `EntryDate`.

- [ ] **Step 2: Controller actions** — all four carry the role policy. A small private helper does the read-only team-resolve + flag check so the four actions stay parse/call/format only:

```csharp
// Helper — read-only team resolve + EE-enabled gate. Returns null team => caller returns the error result.
private async Task<(IActionResult? Error, TeamInfo? Team)> ResolveEarlyEntryTeamAsync(string slug)
{
    var team = await _teamService.GetTeamBySlugAsync(slug);   // ITeamServiceRead; TeamInfo?
    if (team is null) return (NotFound(), null);
    if (!team.EarlyEntryEnabled) return (NotFound(), null);
    return (null, team);
}

[HttpGet("EarlyEntry")]
[Authorize(Policy = PolicyNames.EarlyEntryArtAdminOrAdmin)]
public async Task<IActionResult> EarlyEntry(string slug, CancellationToken ct)
{
    var (error, team) = await ResolveEarlyEntryTeamAsync(slug);
    if (error is not null) return error;

    var grants = await _teamService.GetEarlyEntryGrantsForTeamAsync(team!.Id, ct);

    // Batch-resolve human display names: IUserServiceRead.GetUserInfosAsync ->
    // IReadOnlyDictionary<Guid, UserInfo>; display name is UserInfo.BurnerName.
    var infos = await _userService.GetUserInfosAsync(grants.Select(g => g.UserId).Distinct().ToList(), ct);

    var vm = new TeamEarlyEntryPageViewModel
    {
        TeamId = team.Id,
        TeamName = team.Name,
        Slug = team.Slug,
        Grants = grants.Select(g => new TeamEarlyEntryRowViewModel
        {
            GrantId = g.Id,
            UserId = g.UserId,
            HumanName = infos.GetValueOrDefault(g.UserId)?.BurnerName ?? "",
            EntryDate = g.EntryDate,
            ProjectName = g.ProjectName,
        }).ToList(),
    };
    return View(vm);
}

[HttpPost("EarlyEntry/Add"), ValidateAntiForgeryToken]
[Authorize(Policy = PolicyNames.EarlyEntryArtAdminOrAdmin)]
public async Task<IActionResult> AddEarlyEntry(string slug, AddTeamEarlyEntryInput input, CancellationToken ct)
{
    var (error, team) = await ResolveEarlyEntryTeamAsync(slug);
    if (error is not null) return error;

    var parsed = LocalDatePattern.Iso.Parse(input.EntryDate);
    if (!ModelState.IsValid || !parsed.Success)
    {
        if (!parsed.Success) ModelState.AddModelError(nameof(input.EntryDate), "Enter a date as yyyy-MM-dd.");
        return await EarlyEntry(slug, ct); // re-render with the validation error
    }

    await _teamService.AddEarlyEntryGrantAsync(team!.Id, input.UserId, parsed.Value, input.ProjectName, GetCurrentUserId()!.Value, ct);
    return RedirectToAction(nameof(EarlyEntry), new { slug });
}

[HttpPost("EarlyEntry/Edit"), ValidateAntiForgeryToken]
[Authorize(Policy = PolicyNames.EarlyEntryArtAdminOrAdmin)]
public async Task<IActionResult> EditEarlyEntry(string slug, EditTeamEarlyEntryInput input, CancellationToken ct)
{
    var (error, _) = await ResolveEarlyEntryTeamAsync(slug);
    if (error is not null) return error;

    var parsed = LocalDatePattern.Iso.Parse(input.EntryDate);
    if (!parsed.Success)
    {
        ModelState.AddModelError(nameof(input.EntryDate), "Enter a date as yyyy-MM-dd.");
        return await EarlyEntry(slug, ct);
    }
    await _teamService.EditEarlyEntryGrantAsync(input.GrantId, parsed.Value, input.ProjectName, GetCurrentUserId()!.Value, ct);
    return RedirectToAction(nameof(EarlyEntry), new { slug });
}

[HttpPost("EarlyEntry/Remove"), ValidateAntiForgeryToken]
[Authorize(Policy = PolicyNames.EarlyEntryArtAdminOrAdmin)]
public async Task<IActionResult> RemoveEarlyEntry(string slug, Guid grantId, CancellationToken ct)
{
    var (error, _) = await ResolveEarlyEntryTeamAsync(slug);
    if (error is not null) return error;
    await _teamService.RemoveEarlyEntryGrantAsync(grantId, GetCurrentUserId()!.Value, ct);
    return RedirectToAction(nameof(EarlyEntry), new { slug });
}
```

**Verified facts that make this safe:**
- `TeamAdminController` is declared `[Authorize]` (bare — authenticated only, **no** restrictive class policy) + `[Route("Teams/{slug}")]`. So an action-level `[Authorize(Policy = EarlyEntryArtAdminOrAdmin)]` **AND-combines** to "authenticated AND holds the EE policy" — an `EarlyEntryArtAdmin` is authenticated, so it passes. **No separate controller is needed.**
- Routes are relative to `Teams/{slug}`: use `[HttpGet("EarlyEntry")]`, `[HttpPost("EarlyEntry/Add")]`, `[HttpPost("EarlyEntry/Edit")]`, `[HttpPost("EarlyEntry/Remove")]` (mirror how `Members` writes its relative templates).
- `ITeamServiceRead.GetTeamBySlugAsync(string slug, CancellationToken)` returns `TeamInfo?`. Confirm whether it expects a normalized slug (mirror what `Members`/`ResolveTeamManagementAsync` pass).
- Actor id: the base `HumansControllerBase` exposes `GetCurrentUserId()` → `Guid?` (and `RequireCurrentUserAsync()` → `(IActionResult?, UserInfo)`). Under `[Authorize]` the id claim is present, so `GetCurrentUserId()!.Value` is safe in these actions (used above as the actor).

> Add `using NodaTime.Text;`, `using Humans.Web.Authorization;` (for `PolicyNames`), `using Humans.Domain.Constants;` (for `RoleNames`, if referenced), and `using Microsoft.AspNetCore.Authorization;`. `_teamService` / `_userService` are the controller's injected backing fields (ctor params `teamService`/`userService`) — confirm the exact field names. `TeamInfo` now carries `EarlyEntryEnabled` (Task 8, Step 1b).

- [ ] **Step 3: View** `Views/TeamAdmin/EarlyEntry.cshtml`

- Page title + back link to the team detail page (nav-completeness).
- A table of `Model.Grants`: human name, date, `Art: {ProjectName}`, an inline edit form (date + project) and a remove form (POST `RemoveEarlyEntry` with `grantId`, antiforgery).
- An **Add** form posting to `AddEarlyEntry` containing:
  ```cshtml
  <vc:human-search field-name="UserId" instance-key="team-ee-add"
                   placeholder="@Localizer["TeamAdmin_SearchPlaceholder"]" scope="Name" />
  ```
  (Confirm the `scope` tag-helper attribute accepts the string `"Name"` → `HumanSearchScope.Name`, as the `Members.cshtml` usage does.)
  plus `<input type="date" asp-for="EntryDate">` (posts `yyyy-MM-dd` into the string field) and a text input bound to `ProjectName`.
- User-facing copy says "humans," not "users/members."

- [ ] **Step 4: Build + smoke** — `dotnet build Humans.slnx -v quiet`. Then run the app (`dotnet run --project src/Humans.Web`), enable EE on a team as admin, grant a human the EarlyEntryArtAdmin role (Profile → Add Role), then as that human open the management page and add/edit/remove a grant, and confirm the human appears on the EE roster (`/Shifts/Admin/EarlyEntry`) as `Art: {project}`. (Manual verification — UI is prototype-grade, no browser test required.)

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/TeamEarlyEntryViewModels.cs \
        src/Humans.Web/Views/TeamAdmin/EarlyEntry.cshtml \
        src/Humans.Web/Controllers/TeamAdminController.cs
git commit -m "feat(teams): early-entry management page (add/edit/remove)"
```

---

## Final verification

- [ ] `dotnet build Humans.slnx -v quiet` — clean (no analyzer errors; HUM0025 confirms single-repository ownership of `team_early_entry_grants`).
- [ ] `dotnet test Humans.slnx -v quiet` — all green.
- [ ] EE migration-review gate passed (Task 3).
- [ ] Manual: admin enables flag + grants a human the `EarlyEntryArtAdmin` role → that human sees the card link and adds a grant → shows on `/Shifts/Admin/EarlyEntry` as `Art: {project}`; toggle flag off → roster drops the grant but the row survives a re-enable. Confirm a plain team coordinator (no role) does **not** see the link and gets 403 on the EE URL.
- [ ] GDPR: run a user-data export for a granted human → `TeamEarlyEntry` section present; anonymize that account → grants gone.
- [ ] Use superpowers:requesting-code-review before opening the PR.

## Notes / decisions carried from the spec

- Disable = keep rows, stop contributing (repo filters on `Team.EarlyEntryEnabled`). Flag flip → `InvalidateAll()` (cheap at ~500 users; matches camps' global-EE-date pattern).
- Source label hardcoded `"Art: {ProjectName}"`; project name is free text per grant; same name across people = multiple rows (per approved spec).
- Search scope = any human (`<vc:human-search>` without member restriction).
- No table-ownership unit test — the HUM0025 analyzer enforces it automatically (Peter's "analyzers over tests" rule).
- No new cross-section `ITeamServiceRead` surface; EE management lives on `ITeamService` only.
```
