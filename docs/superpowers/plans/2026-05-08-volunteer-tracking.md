# Volunteer Tracking Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Volunteer Tracking sub-page under `/ShiftDashboard` that surfaces (a) volunteers with gaps in their build-period schedule, (b) volunteers who declared participation + filled in availability but have no signups, and lets coordinators mark "went to camp set-up" dates and block-out individual days. Volunteers can self-block days from `/Shifts/Mine`.

**Architecture:** New Shifts-owned entity `VolunteerBuildStatus` (one row per user per event) carrying optional `BarrioSetupStartDate` and a jsonb list of `BlockedDayOffsets`. New service `IVolunteerTrackingService` builds a `VolunteerTrackingViewModel` with two cohorts (signups-with-gaps, declared-but-unbooked); display sorting is the controller's job. Two write surfaces: `VolunteerTrackingController` (Admin + VolunteerCoordinator, gated by new `VolunteerTrackingWrite` policy) and `ShiftsController.SaveBlockedDays` (current user's own row, UserId from `ClaimsPrincipal`).

**Tech Stack:** .NET 9 Clean Architecture, EF Core (Npgsql + jsonb), NodaTime, ASP.NET MVC + Razor, xUnit, Playwright E2E.

**Spec:** `docs/superpowers/specs/2026-05-07-volunteer-tracking-design.md` — read first.

---

## Project Conventions Quick-Reference

- All `dotnet` commands MUST use `-v quiet` (per `memory/process/dotnet-verbosity-quiet.md`).
- Build: `dotnet build Humans.slnx -v quiet`. Test: `dotnet test Humans.slnx -v quiet`.
- Migrations are 100% auto-generated (per `memory/architecture/no-hand-edited-migrations.md`); `HumansDbContextModelSnapshot.cs` too.
- Cross-section EF nav properties forbidden — `UserId` is a bare `Guid` (per `memory/architecture/no-cross-section-ef-joins.md`).
- Repository methods return materialized `IReadOnlyList<...>` (per `memory/architecture/no-linq-at-db-layer.md`).
- Display sort lives in controllers, not services or repos (per `memory/architecture/display-sort-in-controllers.md`).
- All new methods on the brand-new `IVolunteerTrackingRepository`; **zero new methods** on `IUserService`, `IShiftSignupRepository`, `IShiftManagementRepository`, `IGeneralAvailabilityRepository` (per `memory/architecture/interface-method-budget-ratchet.md`).
- Audit `EntityType` uses `nameof(VolunteerBuildStatus)` (matches existing pattern, e.g. `RoleAssignmentService.cs:136` uses `nameof(User)`).
- `[Authorize(Policy = ...)]`, never inline `IsInRole` chains (per `memory/code/authorization-conventions.md`).
- View partials `@inject IAuthorizationService` and resolve their own gates (per `memory/code/auth-in-views-self-resolving.md`).
- Icons `fa-solid fa-*` only (per `memory/code/icons-fa6-only.md`).
- Controllers inherit `HumansControllerBase`; `GetCurrentUserAsync` / `SetSuccess` / `SetError` (per `memory/code/controller-base-conventions.md`).
- `Frequent commits` — one TDD cycle per commit. Don't batch.

---

## File Map

**New files:**
- `src/Humans.Domain/Entities/VolunteerBuildStatus.cs`
- `src/Humans.Infrastructure/Data/Configurations/Shifts/VolunteerBuildStatusConfiguration.cs`
- `src/Humans.Infrastructure/Migrations/<timestamp>_AddVolunteerBuildStatus.cs` (auto-generated)
- `src/Humans.Application/Interfaces/Repositories/IVolunteerTrackingRepository.cs`
- `src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs`
- `src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingService.cs`
- `src/Humans.Application/Services/Shifts/VolunteerTrackingService.cs`
- `src/Humans.Application/DTOs/VolunteerTrackingViewModel.cs` (and child DTOs: `VolunteerHeatmapRow`, `VolunteerCohortRow`, `VolunteerCellState` enum)
- `src/Humans.Web/Controllers/VolunteerTrackingController.cs`
- `src/Humans.Web/Models/VolunteerTrackingPageViewModel.cs` (sorted/filtered shape for Razor)
- `src/Humans.Web/Models/SetCampSetupForm.cs`
- `src/Humans.Web/Views/VolunteerTracking/Index.cshtml`
- `src/Humans.Web/Views/VolunteerTracking/_VolunteerHeatmap.cshtml`
- `src/Humans.Web/Views/VolunteerTracking/_VolunteerUnbookedHeatmap.cshtml`
- `tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingServiceTests.cs`
- `tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryTests.cs`
- `tests/Humans.Web.Tests/Controllers/VolunteerTrackingControllerTests.cs`
- `tests/e2e/VolunteerTracking.spec.ts` (Playwright)
- `docs/features/47-volunteer-tracking.md`

**Modified files:**
- `src/Humans.Infrastructure/Data/HumansDbContext.cs` — add `DbSet<VolunteerBuildStatus> VolunteerBuildStatuses`.
- `src/Humans.Domain/Enums/AuditAction.cs` — append 5 enum values.
- `src/Humans.Web/Authorization/PolicyNames.cs` — add `VolunteerTrackingWrite` constant.
- `src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs` — register policy.
- `src/Humans.Web/Controllers/ShiftsController.cs` — add `POST /Shifts/Mine/BlockedDays`.
- `src/Humans.Web/Views/Shifts/Mine.cshtml` — add "Days you can't volunteer" panel.
- `src/Humans.Web/Views/ShiftDashboard/Index.cshtml` — add Volunteer Tracking entry-point card.
- `src/Humans.Web/Resources/SharedResource.resx` (+ language variants) — add `VolTrack_*` keys.
- `tests/Humans.Web.Tests/Controllers/ShiftsControllerTests.cs` — add `SaveBlockedDays` tests.
- `docs/sections/Shifts.md` — add `VolunteerBuildStatus` sub-section.
- `docs/architecture/design-rules.md` — §8 add `volunteer_build_statuses` to Shifts table list.
- DI wiring: wherever Shifts services/repos are registered (typically `Program.cs` or a section-specific extension method) — register `IVolunteerTrackingService`, `IVolunteerTrackingRepository`.

---

## Chunk 1: Domain entity, EF config, DbContext, migration

Goal: `VolunteerBuildStatus` exists in the domain layer, is wired into `HumansDbContext`, and a clean auto-generated migration creates `volunteer_build_statuses`. The `design-rules.md` §8 entry lands in the same commit as the migration.

### Task 1.1: Add `AuditAction` enum values

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs`

- [ ] **Step 1.1.1:** Open the enum and append the 5 new values at the end (alphabetical order is not required; the docstring already says "new values can be appended without migration"):

```csharp
    VolunteerCampSetupSet,
    VolunteerCampSetupCleared,
    VolunteerDayBlocked,
    VolunteerDayUnblocked,
    VolunteerOwnBlockedDaysSaved,
```

- [ ] **Step 1.1.2:** Build to confirm compile-clean.

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds, 0 warnings related to AuditAction.

- [ ] **Step 1.1.3:** Commit.

```bash
git add src/Humans.Domain/Enums/AuditAction.cs
git commit -m "Add AuditAction values for VolunteerBuildStatus mutations"
```

### Task 1.2: Create `VolunteerBuildStatus` entity

**Files:**
- Create: `src/Humans.Domain/Entities/VolunteerBuildStatus.cs`

- [ ] **Step 1.2.1:** Write entity. **No nav property to `User`.** EventSettings nav is acceptable since same-section but we omit it per spec ("Nav properties: none (FK only)").

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Per-user, per-event Shifts-owned coordination state used by the Volunteer
/// Tracking page: optional camp-set-up start date plus a list of day offsets
/// the volunteer is blocked out (doctor visit, rest day, etc.).
///
/// One row per (UserId, EventSettingsId). A row with BarrioSetupStartDate=null
/// and empty BlockedDayOffsets is functionally equivalent to no row.
/// </summary>
public class VolunteerBuildStatus
{
    public Guid Id { get; init; }

    /// <summary>
    /// Cross-section linkage — bare Guid (no nav property, no HasOne) per
    /// memory/architecture/no-cross-section-ef-joins.md.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>Same-section FK to event_settings.</summary>
    public Guid EventSettingsId { get; set; }

    /// <summary>
    /// Calendar date the volunteer left for barrio set-up. Null = not yet
    /// on set-up. When set, days at this offset and later render as
    /// CampSetup in the heatmap and never count as gaps.
    /// </summary>
    public LocalDate? BarrioSetupStartDate { get; set; }

    /// <summary>
    /// Day offsets the volunteer is blocked out (doctor, rest day, etc.).
    /// Stored sorted, deduped. Always inside [BuildStartOffset, 0).
    /// jsonb column; pattern matches GeneralAvailability.AvailableDayOffsets.
    /// </summary>
    public List<int> BlockedDayOffsets { get; set; } = new();

    /// <summary>
    /// Optional free-text from the coordinator who set/cleared the
    /// camp set-up date. Block edits do NOT touch this field.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Coordinator who last modified BarrioSetupStartDate. Block edits do
    /// NOT touch this field — block audit trail lives in audit_log.
    /// </summary>
    public Guid? SetByUserId { get; set; }

    /// <summary>When BarrioSetupStartDate was last modified.</summary>
    public Instant? SetAt { get; set; }
}
```

- [ ] **Step 1.2.2:** Build.

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds.

- [ ] **Step 1.2.3:** Commit.

```bash
git add src/Humans.Domain/Entities/VolunteerBuildStatus.cs
git commit -m "Add VolunteerBuildStatus domain entity"
```

### Task 1.3: EF configuration

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/Shifts/VolunteerBuildStatusConfiguration.cs`

- [ ] **Step 1.3.1:** Write the configuration. Mirror the `GeneralAvailability` pattern for the jsonb int-list column.

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Shifts;

internal sealed class VolunteerBuildStatusConfiguration
    : IEntityTypeConfiguration<VolunteerBuildStatus>
{
    public void Configure(EntityTypeBuilder<VolunteerBuildStatus> builder)
    {
        builder.ToTable("volunteer_build_statuses");

        builder.HasKey(x => x.Id);

        // Cross-section: bare Guid, NO HasOne<User>() — per
        // memory/architecture/no-cross-section-ef-joins.md.
        builder.Property(x => x.UserId).IsRequired();

        // Same-section FK to event_settings; no nav property on the entity.
        builder.HasOne<EventSettings>()
            .WithMany()
            .HasForeignKey(x => x.EventSettingsId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.BarrioSetupStartDate);

        builder.Property(x => x.BlockedDayOffsets)
            .HasColumnType("jsonb");

        builder.Property(x => x.Notes).HasMaxLength(500);

        builder.Property(x => x.SetByUserId);
        builder.Property(x => x.SetAt);

        builder.HasIndex(x => new { x.UserId, x.EventSettingsId })
            .IsUnique();
    }
}
```

- [ ] **Step 1.3.2:** Add `DbSet` to `HumansDbContext`.

Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`. Find the existing Shifts-section DbSets (look for `DbSet<GeneralAvailability>` or similar) and add nearby:

```csharp
public DbSet<VolunteerBuildStatus> VolunteerBuildStatuses => Set<VolunteerBuildStatus>();
```

- [ ] **Step 1.3.3:** Build.

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds.

- [ ] **Step 1.3.4:** Commit.

```bash
git add src/Humans.Infrastructure/Data/Configurations/Shifts/VolunteerBuildStatusConfiguration.cs src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "Wire VolunteerBuildStatus into EF: configuration + DbSet"
```

### Task 1.4: Generate migration

**Files:**
- Create (auto-generated): `src/Humans.Infrastructure/Migrations/<timestamp>_AddVolunteerBuildStatus.cs` and `.Designer.cs`
- Modify (auto-generated): `src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs`

- [ ] **Step 1.4.1:** Generate the migration. Do NOT hand-edit anything.

Run:
```bash
dotnet ef migrations add AddVolunteerBuildStatus \
  --project src/Humans.Infrastructure \
  --startup-project src/Humans.Web \
  --verbose
```

Expected: 3 files created/modified under `src/Humans.Infrastructure/Migrations/`.

- [ ] **Step 1.4.2:** Inspect the generated `Up`/`Down`. The `Up` should:
  - `CreateTable` `volunteer_build_statuses` with the columns from the entity;
  - `BlockedDayOffsets` typed as `jsonb`;
  - FK constraint on `EventSettingsId` → `event_settings(Id)` cascade-on-delete;
  - **No FK constraint on `UserId`** (just a bare Guid column — verify no `addForeignKey` line for UserId in the generated SQL);
  - Unique index on `(UserId, EventSettingsId)`.

If anything looks off, fix the entity or configuration and regenerate (`dotnet ef migrations remove` then re-add). Never hand-edit.

- [ ] **Step 1.4.3:** Run the EF migration reviewer agent (per `memory/process/ef-migration-review-gate.md`).

Run: dispatch the agent at `.claude/agents/ef-migration-reviewer.md` with the path of the new migration. Address any findings before committing.

- [ ] **Step 1.4.4:** Build.

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds.

### Task 1.5: Update `design-rules.md` §8 — same commit as the migration

**Files:**
- Modify: `docs/architecture/design-rules.md`

- [ ] **Step 1.5.1:** Open the file, locate the `## §8` (or similar) heading that lists the Shifts-owned tables. Add `volunteer_build_statuses` next to `general_availability` / `volunteer_event_profiles` in the list.

- [ ] **Step 1.5.2:** Commit the migration **and** the §8 update together (per the spec acceptance line: "design-rules.md §8 lists the new table under Shifts (same commit as the migration)").

```bash
git add src/Humans.Infrastructure/Migrations/ docs/architecture/design-rules.md
git commit -m "EF migration: AddVolunteerBuildStatus + design-rules §8"
```

### Chunk 1 review

- [ ] Dispatch plan-document-reviewer. If issues found, fix and re-dispatch.

---

## Chunk 2: Auth policy + DI registration scaffold

Goal: New `VolunteerTrackingWrite` policy registered. DI for the new repository and service is stubbed (interfaces don't exist yet — that's Chunk 3 / 4 — so this chunk only adds the policy and `PolicyNames` constant; DI registration happens at the end of Chunk 4).

### Task 2.1: `PolicyNames.VolunteerTrackingWrite`

**Files:**
- Modify: `src/Humans.Web/Authorization/PolicyNames.cs`

- [ ] **Step 2.1.1:** Add the constant near the existing `ShiftDashboardAccess`:

```csharp
public const string VolunteerTrackingWrite = nameof(VolunteerTrackingWrite);
```

- [ ] **Step 2.1.2:** Build.

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds.

- [ ] **Step 2.1.3:** Commit.

```bash
git add src/Humans.Web/Authorization/PolicyNames.cs
git commit -m "PolicyNames: add VolunteerTrackingWrite constant"
```

### Task 2.2: Register policy

**Files:**
- Modify: `src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs`

- [ ] **Step 2.2.1:** In the same `AddAuthorization` block where `ShiftDashboardAccess` is registered, append:

```csharp
options.AddPolicy(PolicyNames.VolunteerTrackingWrite, policy =>
    policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator));
```

- [ ] **Step 2.2.2:** Build.

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds.

- [ ] **Step 2.2.3:** Commit.

```bash
git add src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs
git commit -m "Register VolunteerTrackingWrite policy (Admin + VolunteerCoordinator)"
```

### Chunk 2 review

- [ ] Dispatch plan-document-reviewer.

---

## Chunk 3: Repository — interface, implementation, integration tests

Goal: `IVolunteerTrackingRepository` exposes the I/O the service needs: get the row, upsert, replace blocked-days list, set/remove a single offset, get all rows for an event, and load eligible Build-period signups for an event. Integration tests use the existing test infrastructure (`TestDbContextFactory`).

### Task 3.1: Define the interface

**Files:**
- Create: `src/Humans.Application/Interfaces/Repositories/IVolunteerTrackingRepository.cs`

- [ ] **Step 3.1.1:** Write the interface.

```csharp
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Shifts-owned I/O for the new volunteer_build_statuses table plus the
/// scoped Build-period signup read used by the gap detector. All methods
/// return materialized lists / nullable rows — no IQueryable leaks.
/// </summary>
public interface IVolunteerTrackingRepository
{
    /// <summary>Fetch the row for (userId, eventSettingsId), or null.</summary>
    Task<VolunteerBuildStatus?> GetAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>All rows for the event keyed by UserId. Empty list if none.</summary>
    Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Upsert (UserId, EventSettingsId): mutate or insert the row's camp set-up
    /// fields. Does NOT touch BlockedDayOffsets. The caller has already
    /// validated barrioSetupStartDate (or null to clear).
    /// </summary>
    Task<VolunteerBuildStatus> UpsertCampSetupAsync(
        Guid userId,
        Guid eventSettingsId,
        LocalDate? barrioSetupStartDate,
        string? notes,
        Guid? setByUserId,
        Instant? setAt,
        CancellationToken ct = default);

    /// <summary>
    /// Replace the entire BlockedDayOffsets list for (userId, eventSettingsId).
    /// Caller has already validated/sorted/deduped.
    /// Returns the prior list for diffing.
    /// </summary>
    Task<IReadOnlyList<int>> ReplaceBlockedDaysAsync(
        Guid userId,
        Guid eventSettingsId,
        IReadOnlyList<int> dayOffsets,
        CancellationToken ct = default);

    /// <summary>
    /// Add or remove a single offset on the user's list. Idempotent.
    /// Returns whether the list actually changed.
    /// </summary>
    Task<bool> SetBlockAsync(
        Guid userId,
        Guid eventSettingsId,
        int dayOffset,
        bool block,
        CancellationToken ct = default);

    /// <summary>
    /// All eligible Build-period signups for the event: rows where
    /// Shift.DayOffset ∈ [BuildStartOffset, 0), the rota's period
    /// is Build or All, and Status ∈ {Confirmed, Pending}.
    /// </summary>
    Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, CancellationToken ct = default);
}

/// <summary>
/// Projection: just what the gap-detector needs for a single eligible signup.
/// RotaName is the parent rota's display name, used by the heatmap partial
/// to populate cell-click popovers.
/// </summary>
public sealed record EligibleBuildSignup(
    Guid UserId,
    int DayOffset,
    SignupStatus Status,
    string RotaName);
```

- [ ] **Step 3.1.2:** Build.

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds (interface only — no implementation yet).

- [ ] **Step 3.1.3:** Commit.

```bash
git add src/Humans.Application/Interfaces/Repositories/IVolunteerTrackingRepository.cs
git commit -m "IVolunteerTrackingRepository interface + EligibleBuildSignup DTO"
```

### Task 3.2: Repository tests — `GetAsync` returns null when no row

**Files:**
- Create: `tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryTests.cs`

- [ ] **Step 3.2.1:** Read `tests/Humans.Integration.Tests/Infrastructure/IntegrationTestBase.cs` to confirm the project's actual test-base pattern. Inherit from it (NOT a `TestDbContextFactory`); the base typically exposes a `DbContext` (or its factory) and supplies a clean DB per test.

- [ ] **Step 3.2.2:** Read one or two existing repository tests under `tests/Humans.Integration.Tests/` (e.g. anything under `Repositories/`) to confirm the seed/teardown shape and the namespace conventions actually in use, then mirror them.

- [ ] **Step 3.2.3:** Write the failing test:

```csharp
using FluentAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Shifts;
using Humans.Integration.Tests.Infrastructure;
using Xunit;

namespace Humans.Integration.Tests.Repositories.Shifts;

public sealed class VolunteerTrackingRepositoryTests : IntegrationTestBase
{
    public VolunteerTrackingRepositoryTests(/* base ctor args per IntegrationTestBase */) { }

    [Fact]
    public async Task GetAsync_returns_null_when_no_row_exists()
    {
        var sut = new VolunteerTrackingRepository(DbContext);
        var result = await sut.GetAsync(Guid.NewGuid(), Guid.NewGuid());
        result.Should().BeNull();
    }
}
```

Replace the constructor signature and the `DbContext` access pattern with whatever the actual `IntegrationTestBase` exposes (the engineer will see this when they read the base class in Step 3.2.1).

- [ ] **Step 3.2.3:** Run — expect compile failure (`VolunteerTrackingRepository` doesn't exist).

Run: `dotnet test tests/Humans.Integration.Tests -v quiet --filter VolunteerTrackingRepositoryTests`
Expected: Build fails with "VolunteerTrackingRepository could not be found".

### Task 3.3: Repository implementation skeleton

**Files:**
- Create: `src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs`

- [ ] **Step 3.3.1:** Skeleton implementation — only enough to make Task 3.2's test pass.

```csharp
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Shifts;

internal sealed class VolunteerTrackingRepository : IVolunteerTrackingRepository
{
    private readonly HumansDbContext _db;
    public VolunteerTrackingRepository(HumansDbContext db) => _db = db;

    public Task<VolunteerBuildStatus?> GetAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default) =>
        _db.VolunteerBuildStatuses
            .FirstOrDefaultAsync(
                x => x.UserId == userId && x.EventSettingsId == eventSettingsId,
                ct);

    public Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<VolunteerBuildStatus> UpsertCampSetupAsync(
        Guid userId, Guid eventSettingsId, LocalDate? barrioSetupStartDate,
        string? notes, Guid? setByUserId, Instant? setAt, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<int>> ReplaceBlockedDaysAsync(
        Guid userId, Guid eventSettingsId, IReadOnlyList<int> dayOffsets,
        CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<bool> SetBlockAsync(
        Guid userId, Guid eventSettingsId, int dayOffset, bool block,
        CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, CancellationToken ct = default) =>
        throw new NotImplementedException();
}
```

- [ ] **Step 3.3.2:** Run the test. Expected: PASS for `GetAsync_returns_null_when_no_row_exists`.

Run: `dotnet test tests/Humans.Integration.Tests -v quiet --filter VolunteerTrackingRepositoryTests`
Expected: 1 passed, 0 failed.

- [ ] **Step 3.3.3:** Commit.

```bash
git add src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryTests.cs
git commit -m "VolunteerTrackingRepository skeleton + first GetAsync test green"
```

### Task 3.4: `UpsertCampSetupAsync` — insert path

- [ ] **Step 3.4.1:** Add failing test in `VolunteerTrackingRepositoryTests`:

```csharp
[Fact]
public async Task UpsertCampSetupAsync_inserts_when_no_row_exists()
{
    var db = DbContext;  // inherited from IntegrationTestBase
    var es = SeedActiveEvent(db);  // small helper local to this test file: creates an EventSettings row, saves, returns it
    var userId = Guid.NewGuid();
    var sut = new VolunteerTrackingRepository(db);

    var result = await sut.UpsertCampSetupAsync(
        userId, es.Id,
        barrioSetupStartDate: new LocalDate(2026, 7, 1),
        notes: "left for barrio",
        setByUserId: Guid.NewGuid(),
        setAt: SystemClock.Instance.GetCurrentInstant());

    result.UserId.Should().Be(userId);
    result.BarrioSetupStartDate.Should().Be(new LocalDate(2026, 7, 1));
    result.Notes.Should().Be("left for barrio");

    var fetched = await db.VolunteerBuildStatuses.SingleAsync();
    fetched.Id.Should().Be(result.Id);
}
```

- [ ] **Step 3.4.2:** Run; expect failure (NotImplementedException).

- [ ] **Step 3.4.3:** Implement `UpsertCampSetupAsync`:

```csharp
public async Task<VolunteerBuildStatus> UpsertCampSetupAsync(
    Guid userId, Guid eventSettingsId, LocalDate? barrioSetupStartDate,
    string? notes, Guid? setByUserId, Instant? setAt, CancellationToken ct = default)
{
    var existing = await _db.VolunteerBuildStatuses
        .FirstOrDefaultAsync(x => x.UserId == userId && x.EventSettingsId == eventSettingsId, ct);

    if (existing is null)
    {
        var row = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = eventSettingsId,
            BarrioSetupStartDate = barrioSetupStartDate,
            Notes = notes,
            SetByUserId = setByUserId,
            SetAt = setAt,
            BlockedDayOffsets = new()
        };
        _db.VolunteerBuildStatuses.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    existing.BarrioSetupStartDate = barrioSetupStartDate;
    existing.Notes = notes;
    existing.SetByUserId = setByUserId;
    existing.SetAt = setAt;
    await _db.SaveChangesAsync(ct);
    return existing;
}
```

- [ ] **Step 3.4.4:** Run test. Expected PASS.

- [ ] **Step 3.4.5:** Commit.

```bash
git add tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryTests.cs src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs
git commit -m "Repo: UpsertCampSetupAsync insert path"
```

### Task 3.5: `UpsertCampSetupAsync` — update path (deferred test until Task 3.6)

This test depends on `ReplaceBlockedDaysAsync` (added in Task 3.6). To preserve TDD bisect cleanliness, write the test in Task 3.6 only — don't commit a pre-failing test now.

- [ ] **Step 3.5.1:** Skip — test is interleaved with Task 3.6 below.

### Task 3.6: `ReplaceBlockedDaysAsync` + idempotent normalization

- [ ] **Step 3.6.1:** Add failing test:

```csharp
[Fact]
public async Task ReplaceBlockedDaysAsync_persists_sorted_deduped_list_and_returns_prior()
{
    var db = DbContext;
    var es = SeedActiveEvent(db);
    var userId = Guid.NewGuid();
    var sut = new VolunteerTrackingRepository(db);

    // Seed an empty row first.
    await sut.UpsertCampSetupAsync(userId, es.Id, null, null, null, null);

    var prior = await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -3, -5, -3 });
    prior.Should().BeEmpty();

    var row = await sut.GetAsync(userId, es.Id);
    row!.BlockedDayOffsets.Should().Equal(-5, -3);   // sorted ascending, deduped

    var prior2 = await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -7 });
    prior2.Should().Equal(-5, -3);
}
```

- [ ] **Step 3.6.2:** Run; expect failure.

- [ ] **Step 3.6.3:** Implement:

```csharp
public async Task<IReadOnlyList<int>> ReplaceBlockedDaysAsync(
    Guid userId, Guid eventSettingsId, IReadOnlyList<int> dayOffsets,
    CancellationToken ct = default)
{
    var existing = await _db.VolunteerBuildStatuses
        .FirstOrDefaultAsync(x => x.UserId == userId && x.EventSettingsId == eventSettingsId, ct);

    var normalized = dayOffsets.Distinct().OrderBy(x => x).ToList();

    if (existing is null)
    {
        var row = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = eventSettingsId,
            BlockedDayOffsets = normalized
        };
        _db.VolunteerBuildStatuses.Add(row);
        await _db.SaveChangesAsync(ct);
        return Array.Empty<int>();
    }

    var prior = existing.BlockedDayOffsets.ToList();
    existing.BlockedDayOffsets = normalized;
    await _db.SaveChangesAsync(ct);
    return prior;
}
```

- [ ] **Step 3.6.4:** Add the deferred Task 3.5 update-path test in the same file:

```csharp
[Fact]
public async Task UpsertCampSetupAsync_updates_existing_row_and_preserves_blocked_days()
{
    var db = DbContext;
    var es = SeedActiveEvent(db);
    var userId = Guid.NewGuid();
    var sut = new VolunteerTrackingRepository(db);

    await sut.UpsertCampSetupAsync(userId, es.Id, new LocalDate(2026, 6, 30), "first", null, null);
    await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -3 });
    await sut.UpsertCampSetupAsync(userId, es.Id, new LocalDate(2026, 7, 1), "second", null, null);

    var rows = await db.VolunteerBuildStatuses.ToListAsync();
    rows.Should().HaveCount(1);
    rows[0].BarrioSetupStartDate.Should().Be(new LocalDate(2026, 7, 1));
    rows[0].Notes.Should().Be("second");
    rows[0].BlockedDayOffsets.Should().Equal(-3);   // preserved
}
```

- [ ] **Step 3.6.5:** Run both tests; expect both PASS.

- [ ] **Step 3.6.6:** Commit.

```bash
git add tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryTests.cs src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs
git commit -m "Repo: ReplaceBlockedDaysAsync (sorted, deduped, returns prior)"
```

### Task 3.7: `SetBlockAsync` (per-day add/remove)

- [ ] **Step 3.7.1:** Add four tests in one block: add when absent, add when present (idempotent), remove when present, remove when absent (idempotent). Each asserts the changed-or-not return value.

- [ ] **Step 3.7.2:** Run; expect failure (NotImplemented).

- [ ] **Step 3.7.3:** Implement:

```csharp
public async Task<bool> SetBlockAsync(
    Guid userId, Guid eventSettingsId, int dayOffset, bool block,
    CancellationToken ct = default)
{
    var existing = await _db.VolunteerBuildStatuses
        .FirstOrDefaultAsync(x => x.UserId == userId && x.EventSettingsId == eventSettingsId, ct);

    if (existing is null)
    {
        if (!block) return false;
        _db.VolunteerBuildStatuses.Add(new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = eventSettingsId,
            BlockedDayOffsets = new() { dayOffset }
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    var contained = existing.BlockedDayOffsets.Contains(dayOffset);
    if (block == contained) return false;

    if (block)
    {
        existing.BlockedDayOffsets.Add(dayOffset);
        existing.BlockedDayOffsets.Sort();
    }
    else
    {
        existing.BlockedDayOffsets.Remove(dayOffset);
    }
    await _db.SaveChangesAsync(ct);
    return true;
}
```

- [ ] **Step 3.7.4:** Run all repo tests. Expected: all PASS.

- [ ] **Step 3.7.5:** Commit.

```bash
git add tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryTests.cs src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs
git commit -m "Repo: SetBlockAsync with idempotent add/remove"
```

### Task 3.8: `GetByEventAsync`

- [ ] **Step 3.8.1:** Add a failing test (seed two events with rows; verify only the requested event's rows are returned).

- [ ] **Step 3.8.2:** Run; expect failure.

- [ ] **Step 3.8.3:** Implement:

```csharp
public async Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(
    Guid eventSettingsId, CancellationToken ct = default) =>
    await _db.VolunteerBuildStatuses
        .Where(x => x.EventSettingsId == eventSettingsId)
        .ToListAsync(ct);
```

- [ ] **Step 3.8.4:** Run test. Expected: PASS.

- [ ] **Step 3.8.5:** Commit.

### Task 3.9: `GetEligibleBuildSignupsAsync`

- [ ] **Step 3.9.1:** Add a failing test that seeds:
  - An active EventSettings with `BuildStartOffset = -10`.
  - A Rota with period `Build` and one shift at `DayOffset = -7`.
  - A Rota with period `Event` and one shift at `DayOffset = 1` (NOT eligible).
  - A Rota with period `Build` and one shift at `DayOffset = -3` and a Confirmed signup.
  - Same `Build` rota also has a shift at `DayOffset = -2` with a Bailed signup (NOT eligible).
  - Verify result contains 1 row (`UserId, -3, Confirmed`) for the seeded user. Verify the Event-period and Bailed signups are excluded.

- [ ] **Step 3.9.2:** Run; expect failure.

- [ ] **Step 3.9.3:** Implement. The materialized projection filters in SQL (no `IQueryable` returned), then materializes to a list of DTOs:

```csharp
public async Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
    Guid eventSettingsId, CancellationToken ct = default)
{
    var es = await _db.EventSettings
        .Where(x => x.Id == eventSettingsId)
        .Select(x => new { x.BuildStartOffset })
        .FirstOrDefaultAsync(ct);

    if (es is null) return Array.Empty<EligibleBuildSignup>();

    var allowedStatuses = new[] { SignupStatus.Confirmed, SignupStatus.Pending };
    var allowedPeriods = new[] { RotaPeriod.Build, RotaPeriod.All };

    return await _db.ShiftSignups
        .Where(s => allowedStatuses.Contains(s.Status))
        .Where(s => s.Shift.DayOffset >= es.BuildStartOffset && s.Shift.DayOffset < 0)
        .Where(s => allowedPeriods.Contains(s.Shift.Rota.Period))
        .Where(s => s.Shift.Rota.EventSettingsId == eventSettingsId)
        .Select(s => new EligibleBuildSignup(
            s.UserId, s.Shift.DayOffset, s.Status, s.Shift.Rota.Name))
        .ToListAsync(ct);
}
```

Note: `ShiftSignup.UserId` is bare-Guid (the `.User` nav was stripped — see `Shifts.md`); we read the FK directly. Same for `Rota` chain — we touch `Shift.Rota.EventSettingsId` and `Period`, both same-section, both non-stripped.

If `Contains(allowedStatuses)` materializes incorrectly because of the string-conversion enum (per `memory/code/no-enum-compare-in-ef.md`), spell out the explicit allowed-values check:

```csharp
.Where(s => s.Status == SignupStatus.Confirmed || s.Status == SignupStatus.Pending)
```

(Same for Period — explicit `==` chain.)

- [ ] **Step 3.9.4:** Run test. Expected: PASS.

- [ ] **Step 3.9.5:** Commit.

```bash
git add tests/Humans.Integration.Tests/Repositories/Shifts/VolunteerTrackingRepositoryTests.cs src/Humans.Infrastructure/Repositories/Shifts/VolunteerTrackingRepository.cs
git commit -m "Repo: GetEligibleBuildSignupsAsync (Build + All rota period, Confirmed + Pending)"
```

### Task 3.10: DI registration

**Files:**
- Modify: wherever Shifts repos are wired (often `Program.cs` or a service-registration extension under `Humans.Web/`). Search for `IShiftSignupRepository` registration.

- [ ] **Step 3.10.1:** Add `services.AddScoped<IVolunteerTrackingRepository, VolunteerTrackingRepository>();` next to `IGeneralAvailabilityRepository` registration.

- [ ] **Step 3.10.2:** Build.

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds.

- [ ] **Step 3.10.3:** Commit.

```bash
git commit -am "DI: register IVolunteerTrackingRepository"
```

### Chunk 3 review

- [ ] Dispatch plan-document-reviewer.

---

## Chunk 4: Service — algorithm + unit tests

Goal: `IVolunteerTrackingService.GetTrackingDataAsync` and the three mutation methods (`SetCampSetupAsync`, `ClearCampSetupAsync`, `SetBlockAsync`) plus `SaveOwnBlockedDaysAsync` for self-service. Service is auth-free; controller does authz. Service does NO display sort. Unit tests use in-memory test doubles for the repo + `IUserService` + `IGeneralAvailabilityRepository` + `IShiftManagementRepository`.

### Task 4.1: ViewModel DTOs

**Files:**
- Create: `src/Humans.Application/DTOs/VolunteerTrackingViewModel.cs`

- [ ] **Step 4.1.1:** Define DTOs.

```csharp
using NodaTime;

namespace Humans.Application.DTOs;

public enum VolunteerCellState
{
    Outside,        // outside active window (main only)
    Confirmed,      // green
    Pending,        // light green
    Gap,            // red — main heatmap only
    Expected,       // grey, future inside active window
    Blocked,        // yellow
    CampSetup,      // blue
    AvailableUnbooked,    // orange — unbooked cohort only
    AvailableExpected,    // light orange — unbooked cohort only
    NotAvailable,         // grey — unbooked cohort only
}

/// <summary>
/// One cell in the heatmap. RotaNames is non-empty only when there is a
/// Confirmed/Pending signup on that day; the partial uses it to render the
/// cell-click popover (which rotas the volunteer is signed up for).
/// </summary>
public sealed record VolunteerCell(
    int DayOffset,
    VolunteerCellState State,
    IReadOnlyList<string> RotaNames);

public sealed record VolunteerHeatmapRow(
    Guid UserId,
    int FirstSignupDay,
    int LastEligibleSignupOffset,
    LocalDate? BarrioSetupStartDate,
    int GapCount,
    IReadOnlyList<VolunteerCell> Cells);

public sealed record VolunteerCohortRow(
    Guid UserId,
    int FirstAvailableDay,
    LocalDate? BarrioSetupStartDate,
    int UnbookedCount,
    IReadOnlyList<VolunteerCell> Cells);

public sealed record VolunteerTrackingViewModel(
    bool HasActiveEvent,
    int BuildStartOffset,
    IReadOnlyList<VolunteerHeatmapRow> MainCohort,
    IReadOnlyList<VolunteerCohortRow> UnbookedCohort);
```

- [ ] **Step 4.1.2:** Build. Commit.

```bash
git add src/Humans.Application/DTOs/VolunteerTrackingViewModel.cs
git commit -m "DTOs for VolunteerTrackingViewModel"
```

### Task 4.2: Service interface

**Files:**
- Create: `src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingService.cs`

- [ ] **Step 4.2.1:** Write interface.

```csharp
using Humans.Application.DTOs;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Shifts;

public interface IVolunteerTrackingService
{
    Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default);

    /// <summary>Coordinator path. Caller has already authorized.</summary>
    Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate barrioSetupStartDate,
        string? notes, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>Coordinator path. Caller has already authorized.</summary>
    Task ClearCampSetupAsync(
        Guid targetUserId, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>Coordinator path. Caller has already authorized.</summary>
    Task<SetBlockResult> SetBlockAsync(
        Guid targetUserId, int dayOffset, bool block,
        Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>
    /// Volunteer-self path. Caller MUST have set ownerUserId from
    /// ClaimsPrincipal — never from the form.
    /// </summary>
    Task<SaveOwnBlockedDaysResult> SaveOwnBlockedDaysAsync(
        Guid ownerUserId, IReadOnlyList<int> dayOffsets, CancellationToken ct = default);

    /// <summary>
    /// Read-side helper for /Shifts/Mine: returns the user's current blocked
    /// offsets plus the build-period bounds for rendering the calendar grid.
    /// Resolves "no active event" / "no row yet" itself.
    /// </summary>
    Task<MineBlockedDaysSummary> GetMineBlockedDaysSummaryAsync(
        Guid userId, CancellationToken ct = default);
}

public sealed record SetCampSetupResult(bool Ok, string? ErrorMessageKey);
public sealed record SetBlockResult(bool Ok, bool Changed, string? ErrorMessageKey);
public sealed record SaveOwnBlockedDaysResult(
    bool Ok, IReadOnlyList<int> Added, IReadOnlyList<int> Removed,
    IReadOnlyList<int> ResultingList, string? ErrorMessageKey);
public sealed record MineBlockedDaysSummary(
    bool HasActiveBuildPeriod,
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    IReadOnlyList<int> BlockedDayOffsets);
```

- [ ] **Step 4.2.2:** Build. Commit.

```bash
git add src/Humans.Application/Interfaces/Shifts/IVolunteerTrackingService.cs
git commit -m "IVolunteerTrackingService interface"
```

### Task 4.3: Service skeleton

**Files:**
- Create: `src/Humans.Application/Services/Shifts/VolunteerTrackingService.cs`

- [ ] **Step 4.3.1:** Skeleton: ctor injecting `IVolunteerTrackingRepository`, `IShiftManagementRepository`, `IGeneralAvailabilityRepository`, `IUserService`, `IClock`. All methods throw `NotImplementedException` initially.

- [ ] **Step 4.3.2:** Build (don't commit — next steps fill in behavior).

### Task 4.4: TDD — `GetTrackingDataAsync` returns empty when no active event

**Files:**
- Create: `tests/Humans.Application.Tests/Services/Shifts/VolunteerTrackingServiceTests.cs`

- [ ] **Step 4.4.1:** Write the test. Use a fake `IShiftManagementRepository` whose `GetActiveEventSettingsAsync` returns null.

```csharp
[Fact]
public async Task GetTrackingDataAsync_returns_empty_when_no_active_event()
{
    var sut = BuildSut(activeEvent: null);
    var result = await sut.GetTrackingDataAsync();
    result.HasActiveEvent.Should().BeFalse();
    result.MainCohort.Should().BeEmpty();
    result.UnbookedCohort.Should().BeEmpty();
}
```

`BuildSut` is a small private helper that wires fake repos with sensible defaults (returning empty lists). Use the project's existing fake-repo style or simple in-class subclasses implementing the interfaces.

- [ ] **Step 4.4.2:** Run; expect compile fail then test fail.

- [ ] **Step 4.4.3:** Implement minimal `GetTrackingDataAsync`:

```csharp
public async Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default)
{
    var es = await _shiftManagement.GetActiveEventSettingsAsync(ct);
    if (es is null)
        return new VolunteerTrackingViewModel(false, 0,
            Array.Empty<VolunteerHeatmapRow>(),
            Array.Empty<VolunteerCohortRow>());

    // … filled in in subsequent tasks
    return new VolunteerTrackingViewModel(true, es.BuildStartOffset,
        Array.Empty<VolunteerHeatmapRow>(),
        Array.Empty<VolunteerCohortRow>());
}
```

- [ ] **Step 4.4.4:** Run. PASS.

- [ ] **Step 4.4.5:** Commit.

### Task 4.5: TDD — single volunteer, fully covered, no gaps

- [ ] **Step 4.5.1:** Write a test that seeds:
  - Active event, `BuildStartOffset = -5`.
  - Today's date set so `todayOffset = -1` (via `FakeClock` fixed to `GateOpeningDate.AtStartOfDayInZone(...)` minus 1 day).
  - One volunteer, `Ticketed`, with Confirmed signups at offsets `-5, -4, -3, -2`.
  - No `VolunteerBuildStatus` row.

  Expect: `MainCohort.Count == 1`, `MainCohort[0].GapCount == 0`, all in-window cells `Confirmed`.

- [ ] **Step 4.5.2:** Run; fail.

- [ ] **Step 4.5.3:** Implement the loading + main-cohort row builder in `GetTrackingDataAsync`. Follow spec §Algorithm steps 2–7 exactly. Pseudocode:

```csharp
var today = _clock.GetCurrentInstant().InZone(DateTimeZoneProviders.Tzdb[es.TimeZoneId]).Date;
var todayOffset = Period.Between(es.GateOpeningDate, today, PeriodUnits.Days).Days;

var signups = await _trackingRepo.GetEligibleBuildSignupsAsync(es.Id, ct);
var statusByUser = await _userService.GetAllParticipationsForYearAsync(es.Year, ct);
var statusMap = statusByUser
    .Where(p => p.Status == ParticipationStatus.NotAttending
             || p.Status == ParticipationStatus.Ticketed
             || p.Status == ParticipationStatus.Attended)
    .ToDictionary(p => p.UserId, p => p.Status);

// Per-user, per-day: (best status across signups on that day, rota names).
// "Best status" prefers Confirmed over Pending because the cell-state branch
// order checks Confirmed first.
var perUserSignups = signups
    .GroupBy(s => s.UserId)
    .ToDictionary(g => g.Key, g => g
        .GroupBy(x => x.DayOffset)
        .ToDictionary(
            dg => dg.Key,
            dg => (
                Status: dg.Any(x => x.Status == SignupStatus.Confirmed)
                    ? SignupStatus.Confirmed : SignupStatus.Pending,
                RotaNames: (IReadOnlyList<string>)dg.Select(x => x.RotaName).Distinct().ToList()
            )));

var bsRows = await _trackingRepo.GetByEventAsync(es.Id, ct);
var bsByUser = bsRows.ToDictionary(r => r.UserId);

var mainRows = new List<VolunteerHeatmapRow>();
foreach (var (userId, daySignups) in perUserSignups)
{
    if (statusMap.TryGetValue(userId, out var st) && st == ParticipationStatus.NotAttending)
        continue;

    var firstSignupDay = daySignups.Keys.Min();
    var lastEligibleSignupOffset = daySignups.Keys.Max();
    bsByUser.TryGetValue(userId, out var bs);
    int? setupOffset = bs?.BarrioSetupStartDate is { } d
        ? Period.Between(es.GateOpeningDate, d, PeriodUnits.Days).Days
        : null;
    var blockedSet = bs?.BlockedDayOffsets.ToHashSet() ?? new HashSet<int>();
    var lastExpectedDay = Math.Min(
        Math.Min(setupOffset ?? int.MaxValue, 0),
        todayOffset + 1);

    var cells = new List<VolunteerCell>(-es.BuildStartOffset);
    int gapCount = 0;
    for (int d2 = es.BuildStartOffset; d2 < 0; d2++)
    {
        VolunteerCellState s;
        IReadOnlyList<string> rotaNames = Array.Empty<string>();
        if (setupOffset.HasValue && d2 >= setupOffset.Value) s = VolunteerCellState.CampSetup;
        else if (d2 < firstSignupDay || d2 >= lastExpectedDay) s = VolunteerCellState.Outside;
        else if (blockedSet.Contains(d2)) s = VolunteerCellState.Blocked;
        else if (daySignups.TryGetValue(d2, out var info))
        {
            s = info.Status == SignupStatus.Confirmed
                ? VolunteerCellState.Confirmed
                : VolunteerCellState.Pending;
            rotaNames = info.RotaNames;
        }
        else if (d2 < todayOffset) { s = VolunteerCellState.Gap; gapCount++; }
        else s = VolunteerCellState.Expected;

        cells.Add(new VolunteerCell(d2, s, rotaNames));
    }

    mainRows.Add(new VolunteerHeatmapRow(
        userId, firstSignupDay, lastEligibleSignupOffset,
        bs?.BarrioSetupStartDate, gapCount, cells));
}

return new VolunteerTrackingViewModel(true, es.BuildStartOffset, mainRows,
    Array.Empty<VolunteerCohortRow>());
```

- [ ] **Step 4.5.4:** Run test. Expected: PASS.

- [ ] **Step 4.5.5:** Commit.

```bash
git commit -am "Service: main cohort algorithm — fully-covered volunteer renders zero gaps"
```

### Task 4.6: TDD — mid-window gap

- [ ] **Step 4.6.1:** Test: signups at `-5, -4, -2` (no `-3`), today is `-1`. Expect: `GapCount == 1`, the cell at `-3` is `Gap`, others as expected.

- [ ] **Step 4.6.2:** Run; PASS (algorithm already supports this).

- [ ] **Step 4.6.3:** Commit.

### Task 4.7: TDD — `NotAttending` excluded

- [ ] **Step 4.7.1:** Test: signups but participation status `NotAttending`. Expect: not in `MainCohort`.

- [ ] **Step 4.7.2:** Run; should already pass given the filter.

- [ ] **Step 4.7.3:** Commit (test only).

### Task 4.8: TDD — Pending signup is light-green, not a gap

- [ ] **Step 4.8.1:** Test: signup at `-3` is `Pending`. Cell at `-3` should be `Pending` not `Gap`.

- [ ] **Step 4.8.2:** Run; PASS.

- [ ] **Step 4.8.3:** Commit.

### Task 4.9: TDD — camp set-up cuts the active window

- [ ] **Step 4.9.1:** Test: signups at `-5, -4`, then `BarrioSetupStartDate = GateOpeningDate.PlusDays(-3)` (so `setupOffset = -3`). Expect: cells at `-5, -4` `Confirmed`, cells at `-3, -2, -1` `CampSetup`. Zero gaps.

- [ ] **Step 4.9.2:** Run; PASS.

- [ ] **Step 4.9.3:** Commit.

### Task 4.10: TDD — block on empty day suppresses the gap

- [ ] **Step 4.10.1:** Test: signups at `-5, -4, -2` (no `-3`), `BlockedDayOffsets = [-3]`. Expect: `GapCount == 0`, cell at `-3` is `Blocked`.

- [ ] **Step 4.10.2:** Run; PASS.

- [ ] **Step 4.10.3:** Commit.

### Task 4.11: TDD — block + camp set-up overlap → CampSetup wins

- [ ] **Step 4.11.1:** Test: `setupOffset = -3`, `BlockedDayOffsets = [-3]`. Cell at `-3` is `CampSetup` (branch order).

- [ ] **Step 4.11.2:** Run; PASS.

- [ ] **Step 4.11.3:** Commit.

### Task 4.12: TDD — declared-but-unbooked cohort

- [ ] **Step 4.12.1:** Test: volunteer A is `Ticketed`, has `GeneralAvailability` with offsets `[-5, -4, -3]`, has zero signups. Today is `-2`. Expect: appears in `UnbookedCohort` with cells at `-5, -4, -3` = `AvailableUnbooked`. `UnbookedCount == 3`. Not in `MainCohort`.

- [ ] **Step 4.12.2:** Run; fail (cohort not implemented).

- [ ] **Step 4.12.3:** Implement the unbooked cohort branch in `GetTrackingDataAsync`:

```csharp
var availabilityRows = await _availability.GetByEventAsync(es.Id, ct);
var availabilityByUser = availabilityRows
    .ToDictionary(g => g.UserId, g => g.AvailableDayOffsets.ToHashSet());

var unbookedRows = new List<VolunteerCohortRow>();
foreach (var participation in statusByUser)
{
    if (participation.Status != ParticipationStatus.Ticketed
        && participation.Status != ParticipationStatus.Attended) continue;

    var userId = participation.UserId;
    if (perUserSignups.ContainsKey(userId)) continue;     // would be in main
    if (!availabilityByUser.TryGetValue(userId, out var avail)) continue;

    var inBuild = avail.Where(d => d >= es.BuildStartOffset && d < 0).ToHashSet();
    if (inBuild.Count == 0) continue;

    var firstAvailableDay = inBuild.Min();
    bsByUser.TryGetValue(userId, out var bs);
    int? setupOffset = bs?.BarrioSetupStartDate is { } d2
        ? Period.Between(es.GateOpeningDate, d2, PeriodUnits.Days).Days
        : null;
    var blockedSet = bs?.BlockedDayOffsets.ToHashSet() ?? new HashSet<int>();

    var cells = new List<VolunteerCell>(-es.BuildStartOffset);
    int unbookedCount = 0;
    for (int d3 = es.BuildStartOffset; d3 < 0; d3++)
    {
        VolunteerCellState s;
        if (setupOffset.HasValue && d3 >= setupOffset.Value) s = VolunteerCellState.CampSetup;
        else if (blockedSet.Contains(d3)) s = VolunteerCellState.Blocked;
        else if (inBuild.Contains(d3) && d3 < todayOffset)
        {
            s = VolunteerCellState.AvailableUnbooked;
            unbookedCount++;
        }
        else if (inBuild.Contains(d3)) s = VolunteerCellState.AvailableExpected;
        else s = VolunteerCellState.NotAvailable;

        // Unbooked cohort never has signups → empty rota-name list.
        cells.Add(new VolunteerCell(d3, s, Array.Empty<string>()));
    }

    unbookedRows.Add(new VolunteerCohortRow(
        userId, firstAvailableDay, bs?.BarrioSetupStartDate, unbookedCount, cells));
}

return new VolunteerTrackingViewModel(true, es.BuildStartOffset, mainRows, unbookedRows);
```

- [ ] **Step 4.12.4:** Run test. PASS.

- [ ] **Step 4.12.5:** Commit.

### Task 4.13: TDD — unbooked → main when first signup appears

- [ ] **Step 4.13.1:** Test: same A as 4.12 but with one Confirmed signup at `-3`. Expect: A in `MainCohort`, NOT in `UnbookedCohort`.

- [ ] **Step 4.13.2:** PASS (the `perUserSignups.ContainsKey` short-circuit).

- [ ] **Step 4.13.3:** Commit (test only).

### Task 4.14: TDD — `NotAttending` excluded from unbooked too

- [ ] **Step 4.14.1:** Test. Should pass given the `Ticketed/Attended`-only filter.

- [ ] **Step 4.14.2:** Commit.

### Task 4.15: TDD — block on available day in unbooked cohort

- [ ] **Step 4.15.1:** Test: A available `[-5, -4]`, blocked `[-5]`, today `-2`. Cells: `-5` = `Blocked`, `-4` = `AvailableUnbooked`.

- [ ] **Step 4.15.2:** PASS.

- [ ] **Step 4.15.3:** Commit.

### Task 4.16: Mutation methods — `SetCampSetupAsync` validation + happy path

- [ ] **Step 4.16.1:** Failing tests:

```csharp
[Fact] public async Task SetCampSetupAsync_rejects_offset_at_or_after_zero() { ... result.Ok.False ... }
[Fact] public async Task SetCampSetupAsync_rejects_date_before_first_signup() { ... }
[Fact] public async Task SetCampSetupAsync_succeeds_inside_build_window() { ... }
```

- [ ] **Step 4.16.2:** Implement `SetCampSetupAsync`:

```csharp
public async Task<SetCampSetupResult> SetCampSetupAsync(
    Guid targetUserId, LocalDate barrioSetupStartDate, string? notes,
    Guid coordinatorUserId, CancellationToken ct = default)
{
    var es = await _shiftManagement.GetActiveEventSettingsAsync(ct)
        ?? throw new InvalidOperationException("No active event");
    var setupOffset = Period.Between(es.GateOpeningDate, barrioSetupStartDate, PeriodUnits.Days).Days;

    if (setupOffset >= 0)
        return new SetCampSetupResult(false, "VolTrack_Err_SetupAtOrAfterGateOpen");

    // Need volunteer's first signup day to validate "must be on or after first signup".
    var signups = await _trackingRepo.GetEligibleBuildSignupsAsync(es.Id, ct);
    var firstSignup = signups
        .Where(s => s.UserId == targetUserId)
        .Select(s => (int?)s.DayOffset)
        .DefaultIfEmpty(null)
        .Min();
    if (firstSignup.HasValue && setupOffset < firstSignup.Value)
        return new SetCampSetupResult(false, "VolTrack_Err_SetupBeforeFirstSignup");

    await _trackingRepo.UpsertCampSetupAsync(
        targetUserId, es.Id, barrioSetupStartDate, notes,
        coordinatorUserId, _clock.GetCurrentInstant(), ct);
    return new SetCampSetupResult(true, null);
}
```

- [ ] **Step 4.16.3:** Run tests. PASS.

- [ ] **Step 4.16.4:** Commit.

### Task 4.17: `ClearCampSetupAsync`

- [ ] **Step 4.17.1:** Failing test: seed a row with set-up date, then clear; row remains, `BarrioSetupStartDate / SetByUserId / SetAt / Notes` all null.

- [ ] **Step 4.17.2:** Implement: call `UpsertCampSetupAsync` with all-null camp-setup params.

- [ ] **Step 4.17.3:** Run; PASS. Commit.

### Task 4.18: `SetBlockAsync` (coordinator path)

- [ ] **Step 4.18.1:** Failing tests: validation rejects offset outside `[BuildStartOffset, 0)`; valid offset returns `Changed = true` first time, `Changed = false` second time.

- [ ] **Step 4.18.2:** Implement:

```csharp
public async Task<SetBlockResult> SetBlockAsync(
    Guid targetUserId, int dayOffset, bool block,
    Guid coordinatorUserId, CancellationToken ct = default)
{
    var es = await _shiftManagement.GetActiveEventSettingsAsync(ct)
        ?? throw new InvalidOperationException("No active event");
    if (dayOffset < es.BuildStartOffset || dayOffset >= 0)
        return new SetBlockResult(false, false, "VolTrack_Err_DayOffsetOutsideBuild");

    var changed = await _trackingRepo.SetBlockAsync(targetUserId, es.Id, dayOffset, block, ct);
    return new SetBlockResult(true, changed, null);
}
```

- [ ] **Step 4.18.3:** Run. PASS. Commit.

### Task 4.19: `SaveOwnBlockedDaysAsync` — diff + replace

- [ ] **Step 4.19.1:** Failing tests:
  - Validation rejects any offset outside `[BuildStartOffset, 0)` → `Ok = false`.
  - Replacing `[-5]` with `[-4, -3]` returns `Added = [-4, -3]`, `Removed = [-5]`, `ResultingList = [-4, -3]`.
  - Sorts and dedupes input.

- [ ] **Step 4.19.2:** Implement:

```csharp
public async Task<SaveOwnBlockedDaysResult> SaveOwnBlockedDaysAsync(
    Guid ownerUserId, IReadOnlyList<int> dayOffsets, CancellationToken ct = default)
{
    var es = await _shiftManagement.GetActiveEventSettingsAsync(ct)
        ?? throw new InvalidOperationException("No active event");

    var normalized = dayOffsets.Distinct().OrderBy(x => x).ToList();
    if (normalized.Any(d => d < es.BuildStartOffset || d >= 0))
        return new SaveOwnBlockedDaysResult(false, Array.Empty<int>(),
            Array.Empty<int>(), Array.Empty<int>(),
            "VolTrack_Err_DayOffsetOutsideBuild");

    var prior = await _trackingRepo.ReplaceBlockedDaysAsync(ownerUserId, es.Id, normalized, ct);
    var priorSet = prior.ToHashSet();
    var newSet = normalized.ToHashSet();
    var added = normalized.Where(d => !priorSet.Contains(d)).ToList();
    var removed = prior.Where(d => !newSet.Contains(d)).ToList();
    return new SaveOwnBlockedDaysResult(true, added, removed, normalized, null);
}
```

- [ ] **Step 4.19.3:** Run. PASS. Commit.

### Task 4.19b: `GetMineBlockedDaysSummaryAsync`

- [ ] **Step 4.19b.1:** Failing tests:
  - No active event → `HasActiveBuildPeriod = false`, empty list, defaults.
  - Active event, no `VolunteerBuildStatus` row → `HasActiveBuildPeriod = true`, empty list, correct `BuildStartOffset` and `GateOpeningDate`.
  - Active event, row with `BlockedDayOffsets = [-3, -1]` → returned as-is.

- [ ] **Step 4.19b.2:** Implement:

```csharp
public async Task<MineBlockedDaysSummary> GetMineBlockedDaysSummaryAsync(
    Guid userId, CancellationToken ct = default)
{
    var es = await _shiftManagement.GetActiveEventSettingsAsync(ct);
    if (es is null || es.BuildStartOffset >= 0)
        return new MineBlockedDaysSummary(false, 0, default, Array.Empty<int>());

    var row = await _trackingRepo.GetAsync(userId, es.Id, ct);
    return new MineBlockedDaysSummary(
        true,
        es.BuildStartOffset,
        es.GateOpeningDate,
        row?.BlockedDayOffsets ?? new List<int>());
}
```

- [ ] **Step 4.19b.3:** Run; PASS. Commit.

### Task 4.20: DI registration for the service

- [ ] **Step 4.20.1:** Add `services.AddScoped<IVolunteerTrackingService, VolunteerTrackingService>();` next to the other Shifts service registrations.

- [ ] **Step 4.20.2:** Build. Commit.

### Chunk 4 review

- [ ] Dispatch plan-document-reviewer.

---

## Chunk 5: Tracking-page controller + tests

### Task 5.1: `SetCampSetupForm`

**Files:**
- Create: `src/Humans.Web/Models/SetCampSetupForm.cs`

- [ ] **Step 5.1.1:** Write per spec lines 145-158:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

public sealed class SetCampSetupForm
{
    [Required] public Guid UserId { get; set; }

    [Required]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string Date { get; set; } = "";

    [StringLength(500)]
    public string? Notes { get; set; }
}
```

- [ ] **Step 5.1.2:** Build. Commit.

### Task 5.2: Controller skeleton with `Index`

**Files:**
- Create: `src/Humans.Web/Controllers/VolunteerTrackingController.cs`

- [ ] **Step 5.2.1:** Write the class. Inherit `HumansControllerBase`. Class-level `[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]`. Inject `IVolunteerTrackingService`, `IUserService`, `IAuditLogService`, and `IStringLocalizer<SharedResource> Localizer`. `HumansControllerBase` does NOT expose these — every existing controller injects its own (`grep -n "IStringLocalizer" src/Humans.Web/Controllers/*.cs` for examples). `Index` action calls `_service.GetTrackingDataAsync()`, sorts both cohorts (per spec), filters per query parameters, projects to `VolunteerTrackingPageViewModel`, returns `View(model)`.

```csharp
[Route("ShiftDashboard/[controller]")]
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
public sealed class VolunteerTrackingController : HumansControllerBase
{
    private readonly IVolunteerTrackingService _service;
    private readonly IUserService _userService;
    private readonly IAuditLogService _auditLogService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public VolunteerTrackingController(
        IVolunteerTrackingService service,
        IUserService userService,
        IAuditLogService auditLogService,
        IStringLocalizer<SharedResource> localizer)
    {
        _service = service;
        _userService = userService;
        _auditLogService = auditLogService;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        bool hideNoGaps = false, bool hideCampSetup = false, bool hideUnbookedSection = false,
        CancellationToken ct = default)
    {
        var data = await _service.GetTrackingDataAsync(ct);
        if (!data.HasActiveEvent) return View(VolunteerTrackingPageViewModel.Empty);

        // Sort happens HERE (controller), per memory/architecture/display-sort-in-controllers.md.
        var displayUserIds = data.MainCohort.Select(r => r.UserId)
            .Concat(data.UnbookedCohort.Select(r => r.UserId))
            .Distinct().ToArray();
        var users = await _userService.GetByIdsAsync(displayUserIds, ct);

        var mainSorted = data.MainCohort
            .Where(r => !hideNoGaps || r.GapCount > 0)
            .Where(r => !hideCampSetup || r.BarrioSetupStartDate is null)
            .OrderByDescending(r => r.GapCount)
            .ThenBy(r => r.LastEligibleSignupOffset)
            .ThenBy(r => DisplayName(users, r.UserId), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unbookedSorted = hideUnbookedSection
            ? new List<VolunteerCohortRow>()
            : data.UnbookedCohort
                .OrderByDescending(r => r.UnbookedCount)
                .ThenBy(r => r.FirstAvailableDay)
                .ThenBy(r => DisplayName(users, r.UserId), StringComparer.OrdinalIgnoreCase)
                .ToList();

        return View(new VolunteerTrackingPageViewModel(
            data.BuildStartOffset, mainSorted, unbookedSorted, users,
            hideNoGaps, hideCampSetup, hideUnbookedSection));
    }

    private static string DisplayName(IReadOnlyDictionary<Guid, User> users, Guid id)
        => users.TryGetValue(id, out var u) ? (u.DisplayName ?? "") : "";
}
```

(Implement `VolunteerTrackingPageViewModel` in `src/Humans.Web/Models/`.)

- [ ] **Step 5.2.2:** Build. Commit.

### Task 5.3: TDD — `Index` returns 200 for VolunteerCoordinator

**Files:**
- Create: `tests/Humans.Web.Tests/Controllers/VolunteerTrackingControllerTests.cs`

- [ ] **Step 5.3.1:** Use the project's existing `WebApplicationFactory<Program>`/`HumansWebTestFixture` pattern. Sign in as a seeded VolunteerCoordinator. `GET /ShiftDashboard/VolunteerTracking` → 200, view name `Index`.

- [ ] **Step 5.3.2:** Run; pass once DI registration in Chunk 4 is in place. Commit.

### Task 5.4: TDD — anonymous redirect to login

- [ ] **Step 5.4.1:** Test: no auth cookie → 302 to login.

- [ ] **Step 5.4.2:** Pass. Commit.

### Task 5.5: TDD — Regular user → 403

- [ ] **Step 5.5.1:** Test: regular volunteer → 403.

- [ ] **Step 5.5.2:** Pass. Commit.

### Task 5.6: `SetCampSetup` action

- [ ] **Step 5.6.1:** Action body:

```csharp
[HttpPost("SetCampSetup")]
[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SetCampSetup(SetCampSetupForm form, CancellationToken ct)
{
    if (!ModelState.IsValid)
    {
        SetError(_localizer["VolTrack_Err_BadRequest"]);
        return RedirectToAction(nameof(Index));
    }

    var parseResult = LocalDatePattern.Iso.Parse(form.Date);
    if (!parseResult.Success)
    {
        SetError(_localizer["VolTrack_Err_BadDate"]);
        return RedirectToAction(nameof(Index));
    }
    var parsed = parseResult.Value;

    var current = await GetCurrentUserAsync();
    if (current is null) return Forbid();

    var result = await _service.SetCampSetupAsync(form.UserId, parsed, form.Notes, current.Id, ct);
    if (!result.Ok)
    {
        SetError(_localizer[result.ErrorMessageKey ?? "VolTrack_Err_Unknown"]);
        return RedirectToAction(nameof(Index));
    }
    await _auditLogService.LogAsync(AuditAction.VolunteerCampSetupSet,
        nameof(VolunteerBuildStatus), form.UserId,
        $"BarrioSetupStartDate set to {form.Date}; notes={form.Notes ?? "—"}",
        current.Id.ToString());
    SetSuccess(_localizer["VolTrack_Msg_CampSetupSaved"]);
    return RedirectToAction(nameof(Index));
}
```

- [ ] **Step 5.6.2:** Tests (per spec controller-tests block):
  - NoInfoAdmin → 403.
  - VolunteerCoordinator + Admin → 302 with success TempData; audit entry written.
  - Form `Date` that fails the `RegularExpression` (e.g. `"not-a-date"`) → ModelState invalid → 302 to Index with `SetError("VolTrack_Err_BadRequest")`.
  - Form `Date` that matches the regex but is not a valid `LocalDate` (e.g. `"2026-13-40"`) → 302 to Index with `SetError("VolTrack_Err_BadDate")` (covers the `parseResult.Success == false` branch).
  - Service-layer rejection (`SetCampSetupAsync` returns `Ok = false`) → 302 to Index with `SetError(localizedReason)`.

- [ ] **Step 5.6.3:** Run; all PASS. Commit.

### Task 5.7: `ClearCampSetup`

- [ ] Mirror Task 5.6 with `ClearCampSetupAsync` + `AuditAction.VolunteerCampSetupCleared`. Tests + implementation + commit.

### Task 5.8: `SetBlock`

- [ ] **Step 5.8.1:** Form:

```csharp
public sealed class SetBlockForm
{
    [Required] public Guid UserId { get; set; }
    [Required] public int DayOffset { get; set; }
    [Required] public bool Block { get; set; }
}
```

- [ ] **Step 5.8.2:** Action:

```csharp
[HttpPost("SetBlock")]
[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SetBlock(SetBlockForm form, CancellationToken ct)
{
    var current = await GetCurrentUserAsync();
    if (current is null) return Forbid();

    var result = await _service.SetBlockAsync(form.UserId, form.DayOffset, form.Block, current.Id, ct);
    if (!result.Ok)
    {
        SetError(_localizer[result.ErrorMessageKey ?? "VolTrack_Err_Unknown"]);
        return BadRequest();
    }
    if (result.Changed)
    {
        await _auditLogService.LogAsync(
            form.Block ? AuditAction.VolunteerDayBlocked : AuditAction.VolunteerDayUnblocked,
            nameof(VolunteerBuildStatus), form.UserId,
            $"DayOffset={form.DayOffset}; by coordinator",
            current.Id.ToString());
    }
    SetSuccess(_localizer[form.Block ? "VolTrack_Msg_DayBlocked" : "VolTrack_Msg_DayUnblocked"]);
    return RedirectToAction(nameof(Index));
}
```

- [ ] **Step 5.8.3:** Tests: validation rejection (offset outside `[BuildStartOffset, 0)`) → BadRequest; happy path → 302 + audit entry.

- [ ] **Step 5.8.4:** Run; PASS. Commit.

### Chunk 5 review

- [ ] Dispatch plan-document-reviewer.

---

## Chunk 6: Tracking-page views

### Task 6.1: `VolunteerTrackingPageViewModel`

- [ ] Create the type with the fields the controller passes to the view (cohorts, users dictionary, filter state, build window). Build. Commit.

### Task 6.2: `Views/VolunteerTracking/Index.cshtml`

- [ ] **Step 6.2.1:** Write the view per spec §Tracking-page view & partials. Include:
  - `<nav class="breadcrumb">` Shifts › Shift Dashboard › Volunteer Tracking.
  - Header card with the four counts and the three filter toggles (form `GET` to `Index`).
  - Conditional empty-state when `Model.MainCohort.Count == 0 && Model.UnbookedCohort.Count == 0`.
  - `<vc:temp-data-alerts />` for success/error.
  - `@await Html.PartialAsync("_VolunteerHeatmap", new HeatmapPartialModel(Model.MainCohort, Model.Users, Model.BuildStartOffset))`.
  - Divider + heading "Declared participating, not booked yet".
  - `@await Html.PartialAsync("_VolunteerUnbookedHeatmap", new UnbookedHeatmapPartialModel(...))`.
  - Footer legend.

- [ ] **Step 6.2.2:** All strings via `@Localizer["VolTrack_*"]`.

- [ ] **Step 6.2.3:** Run dev server, navigate as VolunteerCoordinator, sanity-check rendering. Commit.

### Task 6.3: `_VolunteerHeatmap.cshtml`

- [ ] **Step 6.3.1:** Razor partial. `@inject IAuthorizationService AuthService` + `@{ bool canWrite = (await AuthService.AuthorizeAsync(User, PolicyNames.VolunteerTrackingWrite)).Succeeded; }`.

- [ ] **Step 6.3.2:** Sticky-left column with `<vc:user-avatar />` + display name + gap-count badge + set-up date if any.

- [ ] **Step 6.3.3:** For each day-offset column, render a `<button>` (or `<td>` with role=button) styled by the cell state. Cell state → CSS class:
  - `Confirmed` → `bg-success`
  - `Pending` → `bg-success-subtle`
  - `Gap` → `bg-danger`
  - `Blocked` → `bg-warning-subtle`
  - `CampSetup` → `bg-primary`
  - `Outside` / `Expected` → `bg-secondary-subtle`

- [ ] **Step 6.3.4:** Cell click opens a Bootstrap popover (or a modal) containing:
  - Date label rendered via `ToDisplayDate(date)` (per `memory/code/datetime-display-formatting.md` — never inline format strings).
  - Signup rota names — read directly from `cell.RotaNames` (already populated by the service in Chunk 4 — `VolunteerCell.RotaNames` is the third constructor arg). Empty list = render nothing.
  - When `canWrite`: a `SetCampSetupForm` form (POST to `/ShiftDashboard/VolunteerTracking/SetCampSetup`), and a `SetBlockForm` toggle button (POST to `/ShiftDashboard/VolunteerTracking/SetBlock`).

- [ ] **Step 6.3.5:** Icons `fa-solid fa-*` only (e.g. `fa-solid fa-check`, `fa-solid fa-ban`).

- [ ] **Step 6.3.6:** Sanity-check in browser. Commit.

### Task 6.4: `_VolunteerUnbookedHeatmap.cshtml`

- [ ] **Step 6.4.1:** Razor partial. Same shape as Task 6.3 (sticky-left column, day-offset columns, popover on click, `@inject IAuthorizationService AuthService` + `canWrite` resolution, `fa-solid fa-*` icons only, all strings via `Localizer["VolTrack_*"]`).

- [ ] **Step 6.4.2:** Sticky left column: `<vc:user-avatar />`, display name, "available days" count badge, "unbooked" count badge.

- [ ] **Step 6.4.3:** Cell color mapping:
  - `AvailableUnbooked` → `bg-warning`
  - `AvailableExpected` → `bg-warning-subtle`
  - `Blocked` → `bg-warning-subtle striped` (custom CSS class for diagonal stripe to differentiate from `AvailableExpected`)
  - `CampSetup` → `bg-primary`
  - `NotAvailable` → `bg-secondary-subtle`

- [ ] **Step 6.4.4:** Cell click → popover with:
  - Date label rendered via `ToDisplayDate(date)`.
  - "Available" badge if the cell state is `AvailableUnbooked` or `AvailableExpected`.
  - When `canWrite`: same `SetBlockForm` toggle and `SetCampSetupForm` controls as Task 6.3.

- [ ] **Step 6.4.5:** Sanity-check in browser. Commit.

### Task 6.5: Dashboard entry point

**Files:**
- Modify: `src/Humans.Web/Views/ShiftDashboard/Index.cshtml`

- [ ] **Step 6.5.1:** Just below the existing `<h2>` row and before the period filter row, add a card/link to `/ShiftDashboard/VolunteerTracking`. Use the existing Bootstrap card style; localize the label.

- [ ] **Step 6.5.2:** Sanity-check. Commit.

### Chunk 6 review

- [ ] Dispatch plan-document-reviewer.

---

## Chunk 7: Volunteer self-service

### Task 7.1: Add `SaveBlockedDays` to `ShiftsController`

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`

- [ ] **Step 7.1.1:** New action:

```csharp
[HttpPost("Mine/BlockedDays")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SaveBlockedDays([FromForm] List<int>? dayOffsets, CancellationToken ct)
{
    var current = await GetCurrentUserAsync();
    if (current is null) return Challenge();

    var input = dayOffsets ?? new();
    var result = await _trackingService.SaveOwnBlockedDaysAsync(current.Id, input, ct);
    if (!result.Ok)
    {
        SetError(Localizer[result.ErrorMessageKey ?? "VolTrack_Err_Unknown"]);
        return RedirectToAction(nameof(Mine));
    }

    foreach (var d in result.Added)
    {
        await _audit.LogAsync(AuditAction.VolunteerDayBlocked,
            nameof(VolunteerBuildStatus), current.Id,
            $"DayOffset={d}; self", current.Id.ToString());
    }
    foreach (var d in result.Removed)
    {
        await _audit.LogAsync(AuditAction.VolunteerDayUnblocked,
            nameof(VolunteerBuildStatus), current.Id,
            $"DayOffset={d}; self", current.Id.ToString());
    }
    await _audit.LogAsync(AuditAction.VolunteerOwnBlockedDaysSaved,
        nameof(VolunteerBuildStatus), current.Id,
        $"Resulting list: [{string.Join(",", result.ResultingList)}]",
        current.Id.ToString());

    SetSuccess(Localizer["VolTrack_Msg_BlockedDaysSaved"]);
    return RedirectToAction(nameof(Mine));
}
```

- [ ] **Step 7.1.2:** Inject `IVolunteerTrackingService` into `ShiftsController` ctor.

- [ ] **Step 7.1.3:** Build. Commit.

### Task 7.2: TDD — regression test for the userId-from-form attack vector

- [ ] **Step 7.2.1:** Test in `tests/Humans.Web.Tests/Controllers/ShiftsControllerTests.cs`: signed in as User A, POST form with `UserId=B` and `dayOffsets=[-3]`. Verify the row written is for A, not B. (i.e., `UserId` in the form is *ignored*.)

- [ ] **Step 7.2.2:** Run; PASS — the form has no `UserId` field, so even if a malicious client injects it, model binding ignores it. Commit.

### Task 7.3: Other controller tests for `SaveBlockedDays`

- [ ] Anonymous → redirect to login.
- [ ] Regular user → 302 to Mine with success TempData.
- [ ] Submitting unsorted/duplicated offsets → repository row is sorted/deduped.
- [ ] Bulk save emits `VolunteerOwnBlockedDaysSaved` plus per-diff `VolunteerDayBlocked`/`VolunteerDayUnblocked`.

Each test → run → PASS → commit.

### Task 7.4: `Mine.cshtml` — "Days you can't volunteer" panel

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Mine.cshtml`

- [ ] **Step 7.4.1:** Add panel below the existing availability section:

```cshtml
@if (Model.HasActiveBuildPeriod)
{
    <div class="card mt-4">
        <div class="card-body">
            <h5 class="card-title">
                <i class="fa-solid fa-ban"></i>
                @Localizer["VolTrack_Mine_BlockedTitle"]
            </h5>
            <p class="text-muted small">@Localizer["VolTrack_Mine_BlockedHelp"]</p>

            <form method="post" asp-action="SaveBlockedDays" asp-controller="Shifts">
                @Html.AntiForgeryToken()
                <div class="d-flex flex-wrap gap-1">
                    @for (int d = Model.BuildStartOffset; d < 0; d++)
                    {
                        bool isBlocked = Model.BlockedDayOffsets.Contains(d);
                        var date = Model.GateOpeningDate.PlusDays(d);
                        <label class="btn btn-sm @(isBlocked ? "btn-warning" : "btn-outline-secondary")">
                            <input type="checkbox" name="dayOffsets" value="@d" autocomplete="off"
                                   @(isBlocked ? "checked" : "") class="d-none" />
                            @date.ToDisplayDate()
                        </label>
                    }
                </div>
                <button type="submit" class="btn btn-primary mt-2">
                    @Localizer["VolTrack_Mine_BlockedSave"]
                </button>
            </form>
        </div>
    </div>
}
```

- [ ] **Step 7.4.2:** Add `BlockedDayOffsets`, `HasActiveBuildPeriod`, `BuildStartOffset`, `GateOpeningDate` to the `ShiftsController.Mine` view model. Resolve them via `IVolunteerTrackingService` — add a small method `Task<MineBlockedDaysSummary> GetMineBlockedDaysSummaryAsync(Guid userId, CancellationToken ct)` to `IVolunteerTrackingService` (this is a brand-new interface so growing it does not touch any budgeted interface). The service handles "no active event" / "no row yet" and returns a populated DTO. Service does the I/O via `IVolunteerTrackingRepository.GetAsync` and `IShiftManagementRepository.GetActiveEventSettingsAsync`. Controllers should orchestrate via services, not reach into repositories directly for view-model assembly.

- [ ] **Step 7.4.3:** Sanity-check in browser. Commit.

### Chunk 7 review

- [ ] Dispatch plan-document-reviewer.

---

## Chunk 8: Localization, docs, E2E, finalization

### Task 8.1: Localization keys

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.resx` and language variants (`ca`, `cs`, `de`, `es`, `fr`, etc.).

- [ ] **Step 8.1.1:** Add all `VolTrack_*` keys used in views and controller code paths. English first; non-English variants get the same English value to start (Localization Coordinator translates later — pattern matches existing additions).

Required keys (verify against the views):

```
VolTrack_PageTitle
VolTrack_Breadcrumb_Tracking
VolTrack_Header_VolunteersCount
VolTrack_Header_GapCount
VolTrack_Header_CampSetupCount
VolTrack_Header_UnbookedCount
VolTrack_Filter_HideNoGaps
VolTrack_Filter_HideCampSetup
VolTrack_Filter_HideUnbooked
VolTrack_Section_Unbooked_Title
VolTrack_Legend_Confirmed
VolTrack_Legend_Pending
VolTrack_Legend_Gap
VolTrack_Legend_Blocked
VolTrack_Legend_CampSetup
VolTrack_Legend_AvailableUnbooked
VolTrack_Legend_AvailableExpected
VolTrack_Legend_NotAvailable
VolTrack_Action_MarkCampSetup
VolTrack_Action_ClearCampSetup
VolTrack_Action_BlockDay
VolTrack_Action_UnblockDay
VolTrack_Mine_BlockedTitle
VolTrack_Mine_BlockedHelp
VolTrack_Mine_BlockedSave
VolTrack_Msg_CampSetupSaved
VolTrack_Msg_CampSetupCleared
VolTrack_Msg_DayBlocked
VolTrack_Msg_DayUnblocked
VolTrack_Msg_BlockedDaysSaved
VolTrack_Err_BadRequest
VolTrack_Err_BadDate
VolTrack_Err_SetupAtOrAfterGateOpen
VolTrack_Err_SetupBeforeFirstSignup
VolTrack_Err_DayOffsetOutsideBuild
VolTrack_Err_Unknown
VolTrack_AuditAction_CampSetupSet
VolTrack_AuditAction_CampSetupCleared
VolTrack_AuditAction_DayBlocked
VolTrack_AuditAction_DayUnblocked
VolTrack_AuditAction_OwnBlockedDaysSaved
VolTrack_NoActiveEvent
```

- [ ] **Step 8.1.2:** Build (resgen). Run dev server, sanity-check page renders without missing-key warnings.

- [ ] **Step 8.1.3:** Commit.

### Task 8.2: `docs/sections/Shifts.md` — add `VolunteerBuildStatus` sub-section

**Files:**
- Modify: `docs/sections/Shifts.md`

- [ ] **Step 8.2.1:** In the §Data Model section, add a sub-section after `### GeneralAvailability` describing `VolunteerBuildStatus`: fields, table, indices, FK style (cross-section bare Guid for UserId), purpose. Mirror the style of the existing `### GeneralAvailability` and `### VolunteerEventProfile` blocks.

- [ ] **Step 8.2.2:** Commit.

### Task 8.3: `docs/features/47-volunteer-tracking.md`

**Files:**
- Create: `docs/features/47-volunteer-tracking.md`

- [ ] **Step 8.3.1:** Write the feature spec (per `memory/process/feature-spec-on-new-feature.md`):
  - **Business context** — VC needs to identify volunteers who arrived for build then dropped off; mark camp set-up; let volunteers block out doctor visits / rest days.
  - **User stories with acceptance criteria** — three stories: (a) VC sees gap; (b) volunteer self-blocks day; (c) VC marks camp set-up.
  - **Data model** — `VolunteerBuildStatus` summary; cross-link to `Shifts.md`.
  - **Workflows** — heatmap algorithm at a high level; the two cohorts.
  - **Related features** — cross-link to `25-shift-management.md`, `26-shift-signup-visibility.md`, `event-participation.md`.

- [ ] **Step 8.3.2:** Commit.

### Task 8.4: E2E — Playwright tests

**Files:**
- Create: `tests/e2e/tests/volunteer-tracking.spec.ts` (path/name matches the project's existing E2E naming — see `tests/e2e/tests/shifts.spec.ts`, `teams.spec.ts`, etc. — lowercase under `tests/e2e/tests/`).

- [ ] **Step 8.4.1:** Two flows from the spec §E2E tests:
  - VC flow: sign in as VC, navigate, identify red cell, mark camp set-up, verify blue, clear, verify revert; block / unblock cell; scroll to unbooked section.
  - Volunteer flow: sign in as regular volunteer, `/Shifts/Mine`, toggle blocked days, save, sign back in as VC, confirm yellow cells.

- [ ] **Step 8.4.2:** Test seeds: extend the dashboard's existing dev seeder (`/dev/seed/dashboard` per the `Index.cshtml` you saw earlier) to include a known volunteer with the gap pattern.

- [ ] **Step 8.4.3:** Run the E2E suite per the project's existing `tests/e2e/README.md` instructions.

- [ ] **Step 8.4.4:** Commit.

### Task 8.5: Final build/test sweep

- [ ] **Step 8.5.1:** `dotnet build Humans.slnx -v quiet` — clean.
- [ ] **Step 8.5.2:** `dotnet test Humans.slnx -v quiet` — all green.
- [ ] **Step 8.5.3:** Run `/freshness-sweep` to regenerate any drift-prone docs (per `CLAUDE.md`).
- [ ] **Step 8.5.4:** Push branch. The remote is `origin` per `CLAUDE.md` (`origin = peterdrier/Humans`). If the local clone uses a different remote name (e.g. a contributor's `fork` for their own GitHub fork), substitute that name; default to `origin`.

```bash
git push -u origin feat/volunteer-tracking
```

- [ ] **Step 8.5.5:** Open PR against `peterdrier/Humans` `main` (per `memory/process/no-direct-to-main.md`).

### Chunk 8 review

- [ ] Dispatch plan-document-reviewer.

---

## Acceptance — final

Before marking the PR ready:

- [ ] All Acceptance items from the spec checked.
- [ ] `dotnet build Humans.slnx -v quiet` clean.
- [ ] `dotnet test Humans.slnx -v quiet` clean.
- [ ] EF migration reviewer agent has signed off.
- [ ] `docs/features/47-volunteer-tracking.md` committed in the same PR.
- [ ] `docs/sections/Shifts.md` updated.
- [ ] `docs/architecture/design-rules.md` §8 updated.
- [ ] Localization keys added across language variants.
- [ ] No new methods on `IUserService`, `IShiftSignupRepository`, `IShiftManagementRepository`, `IGeneralAvailabilityRepository` — confirm by grepping the diff.
- [ ] No `[Authorize(Roles = ...)]` inline role lists — only `[Authorize(Policy = ...)]`.
- [ ] No `bi bi-*` icons — only `fa-solid fa-*`.
- [ ] All controllers inherit `HumansControllerBase`; no raw `TempData[…]` or `_userManager`.
- [ ] No `OrderBy` in service or repository methods — sorting in the controller.
