# Barrio Shift Obligations — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let CampAdmins track and prompt each barrio's shift obligations to function teams (Power = a Team; Shit-ninja = a Rota in LnT), and let barrio leads see who from their barrio has signed up.

**Architecture:** The **Camps** section owns the feature: two new Camps-owned tables (`shift_obligations` config + sparse `camp_season_shift_obligations` overrides), a `ShiftObligationService` that orchestrates the compliance matrix by reading barrio membership/leads/grid (Camps' own data) and confirmed-signup counts from the **Shifts** section via a new minimal **`IShiftServiceRead`** cross-section interface (resolves the active event internally; supports Team-scoped and Rota-scoped counts). Reminder emails go to barrio leads + the function's camp-role holder. UI is a CampAdmin matrix with drill-down detail, plus a scoped read-only detail for barrio leads.

**Tech Stack:** .NET, EF Core (Npgsql), NodaTime, Clean Architecture (Domain → Application → Infrastructure → Web), xUnit + FluentAssertions, Razor views, Bootstrap.

**Spec:** `docs/superpowers/specs/2026-06-04-barrio-shift-obligations-design.md`

## ⚠️ Approval gate (read before starting)

- **Chunk 2 (`IShiftServiceRead`)** introduces a new public cross-section interface in the Shifts section. Per the reuse-first discipline and Peter's hard rules, **new interface surface needs Peter's approval before implementation.** Do not merge Chunk 2 until approved. Chunk 3+ depend on it — if approval is pending, stub `IShiftServiceRead` behind an interface the executor controls and proceed; swap to the real impl once approved.
- **Every migration** (Chunk 1) must pass the EF migration-review gate (`.claude/agents/ef-migration-reviewer.md`, `memory/process/ef-migration-review-gate.md`) before commit.
- **New user-facing pages** (Chunk 5) → run the nav-completeness check; add the link from `/Camps/Admin`.
- Build/test with `-v quiet` (`memory/process/dotnet-verbosity-quiet.md`): `dotnet build Humans.slnx -v quiet`, `dotnet test Humans.slnx -v quiet`.
- Use `@superpowers:test-driven-development` for the logic-bearing tasks (computation, count queries, recipient resolution, applicability). Skip TDD for pure boilerplate (DI wiring, views).

## File structure (created / modified)

**Domain** (`src/Humans.Domain`)
- Create `Entities/ShiftObligation.cs`, `Entities/CampSeasonShiftObligation.cs`
- Create `Enums/ShiftObligationTargetType.cs`, `Enums/ObligationApplicability.cs`
- Modify `Enums/AuditAction.cs` (append `BarrioShiftReminderSent`)

**Application** (`src/Humans.Application`)
- Create `Interfaces/Shifts/IShiftServiceRead.cs`, `Interfaces/Shifts/RotaTargetInfo.cs`
- Create `Interfaces/Camps/IShiftObligationService.cs` (+ projection records `BarrioObligationMatrix`, `BarrioObligationDetail`)
- Create `Interfaces/Repositories/IShiftObligationRepository.cs`
- Modify `Interfaces/Repositories/ICampRepository.Roles.cs` (role-holder-by-slug read)
- Modify `Interfaces/Shifts/IShiftManagementService.cs` is **not** touched; add to repo interface `Interfaces/Repositories/IShiftManagementRepository.cs`
- Modify `Interfaces/Email/IEmailMessageFactory.cs`, `Services/Email/EmailMessageFactory.cs`, `Interfaces/Email/IEmailRenderer.cs`
- Create `Services/Camps/ShiftObligationService.cs`

**Infrastructure** (`src/Humans.Infrastructure`)
- Create `Repositories/Camps/ShiftObligationRepository.cs`
- Create `Data/Configurations/Camps/ShiftObligationConfiguration.cs`, `Data/Configurations/Camps/CampSeasonShiftObligationConfiguration.cs`
- Modify `Data/HumansDbContext.cs` (two DbSets)
- Modify `Repositories/Shifts/ShiftRepository.Management.cs` (+ count/target reads) and `Repositories/Camps/CampRepository.Roles.cs`
- Modify `Services/EmailRenderer.cs` (+ render method)
- Create `Migrations/<timestamp>_AddBarrioShiftObligations.cs` (generated)

**Web** (`src/Humans.Web`)
- Modify `Controllers/CampAdminController.cs` (matrix, detail, remind, override actions)
- Modify `Controllers/CampController.cs` (barrio-lead `/Camps/{slug}/ShiftObligations`)
- Create `Models/CampAdmin/ShiftObligation*ViewModel.cs`
- Create views `Views/CampAdmin/ShiftObligations.cshtml`, `ShiftObligationDetail.cshtml`, `ShiftObligationFunctions.cshtml`, `Views/Camp/ShiftObligations.cshtml`
- Modify `Extensions/Sections/CampsSectionExtensions.cs`, `Extensions/Sections/ShiftsSectionExtensions.cs` (DI)
- Modify `Properties/SharedResource.resx` (+ `.es.resx`) (email + page strings)
- Modify `Views/CampAdmin/Index.cshtml` (nav link) and `Views/Camp/Members.cshtml` (lead link)

**Tests** (`tests/Humans.Application.Tests`)
- Create `Architecture/ShiftObligationArchitectureTests.cs`
- Create `Camps/ShiftObligationServiceTests.cs`
- Create `Shifts/ShiftServiceReadTests.cs` (+ real-DB integration for the count query)

---

## Chunk 1: Domain + data model + migration

### Task 1.1: Domain enums

**Files:** Create `src/Humans.Domain/Enums/ShiftObligationTargetType.cs`, `src/Humans.Domain/Enums/ObligationApplicability.cs`

- [ ] **Step 1: Create the enums** (no test — trivial enums)

```csharp
// ShiftObligationTargetType.cs
namespace Humans.Domain.Enums;

public enum ShiftObligationTargetType
{
    Team = 0,
    Rota = 1,
}
```

```csharp
// ObligationApplicability.cs
namespace Humans.Domain.Enums;

public enum ObligationApplicability
{
    AllBarrios = 0,
    ElectricalGridConnected = 1,
}
```

- [ ] **Step 2: Build** — `dotnet build Humans.slnx -v quiet` → success.
- [ ] **Step 3: Commit** — `git commit -m "feat(domain): add ShiftObligation enums"`

### Task 1.2: Domain entities

**Files:** Create `src/Humans.Domain/Entities/ShiftObligation.cs`, `src/Humans.Domain/Entities/CampSeasonShiftObligation.cs`

- [ ] **Step 1: Create `ShiftObligation`** (follow `CampMember.cs` pattern: plain class, `init` Ids, `Instant` timestamps)

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

public class ShiftObligation
{
    public Guid Id { get; init; }
    public ShiftObligationTargetType TargetType { get; set; }
    public Guid TargetId { get; set; }                 // TeamId or RotaId per TargetType
    public string CampRoleSlug { get; set; } = string.Empty;
    public ObligationApplicability Applicability { get; set; } = ObligationApplicability.AllBarrios;
    public int DefaultRequiredShiftCount { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant? UpdatedAt { get; set; }

    public ICollection<CampSeasonShiftObligation> Overrides { get; set; } = [];
}
```

```csharp
// CampSeasonShiftObligation.cs
namespace Humans.Domain.Entities;

public class CampSeasonShiftObligation
{
    public Guid Id { get; init; }
    public Guid CampSeasonId { get; init; }
    public Guid ShiftObligationId { get; init; }
    public ShiftObligation ShiftObligation { get; set; } = null!;
    public int RequiredShiftCount { get; set; }
}
```

- [ ] **Step 2: Build** → success.
- [ ] **Step 3: Commit** — `git commit -m "feat(domain): add ShiftObligation + override entities"`

### Task 1.3: EF configuration + DbSets

**Files:** Create `src/Humans.Infrastructure/Data/Configurations/Camps/ShiftObligationConfiguration.cs`, `.../CampSeasonShiftObligationConfiguration.cs`; Modify `src/Humans.Infrastructure/Data/HumansDbContext.cs`

- [ ] **Step 1: Add DbSets to `HumansDbContext`** (next to the Camps DbSets)

```csharp
public DbSet<ShiftObligation> ShiftObligations => Set<ShiftObligation>();
public DbSet<CampSeasonShiftObligation> CampSeasonShiftObligations => Set<CampSeasonShiftObligation>();
```

- [ ] **Step 2: Create `ShiftObligationConfiguration`** (model on `CampMemberConfiguration`; **`IsActive` default-true must use the sentinel-safe form** per the EF reviewer)

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Camps;

public class ShiftObligationConfiguration : IEntityTypeConfiguration<ShiftObligation>
{
    public void Configure(EntityTypeBuilder<ShiftObligation> builder)
    {
        builder.ToTable("shift_obligations");
        builder.Property(o => o.TargetType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.CampRoleSlug).HasMaxLength(64).IsRequired();
        builder.Property(o => o.Applicability).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(o => o.IsActive).IsRequired().HasDefaultValue(true).HasSentinel(true); // bool default-true: sentinel-safe
        builder.HasIndex(o => new { o.TargetType, o.TargetId }).IsUnique()
            .HasDatabaseName("IX_shift_obligations_target_unique");
    }
}
```

- [ ] **Step 3: Create `CampSeasonShiftObligationConfiguration`**

```csharp
public class CampSeasonShiftObligationConfiguration : IEntityTypeConfiguration<CampSeasonShiftObligation>
{
    public void Configure(EntityTypeBuilder<CampSeasonShiftObligation> builder)
    {
        builder.ToTable("camp_season_shift_obligations");
        builder.HasOne(o => o.ShiftObligation)
            .WithMany(o => o.Overrides)
            .HasForeignKey(o => o.ShiftObligationId)
            .OnDelete(DeleteBehavior.Cascade);
        // CampSeason FK is by Guid only (no nav added to CampSeason to avoid touching that entity);
        // configure the relationship to camp_seasons with cascade delete:
        builder.HasOne<CampSeason>().WithMany()
            .HasForeignKey(o => o.CampSeasonId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(o => new { o.CampSeasonId, o.ShiftObligationId }).IsUnique()
            .HasDatabaseName("IX_camp_season_shift_obligations_unique");
        builder.HasIndex(o => o.CampSeasonId);
    }
}
```

- [ ] **Step 4: Build** → success (confirms configs are discovered if the project auto-applies `IEntityTypeConfiguration`; if `ApplyConfigurationsFromAssembly` isn't used, register them where the other Camps configs are registered — check `HumansDbContext.OnModelCreating`).
- [ ] **Step 5: Commit** — `git commit -m "feat(infra): EF config + DbSets for shift obligations"`

### Task 1.4: Migration

**Files:** Generated under `src/Humans.Infrastructure/Migrations/`

- [ ] **Step 1: Generate the migration**

Run:
```bash
dotnet ef migrations add AddBarrioShiftObligations \
  --project src/Humans.Infrastructure --startup-project src/Humans.Web --output-dir Migrations
```
Expected: a `<timestamp>_AddBarrioShiftObligations.cs` + designer + snapshot diff. **Do not hand-edit** beyond what the reviewer requires (`memory/architecture/no-hand-edited-migrations.md`).

- [ ] **Step 2: Snapshot diff sanity** — confirm only the two new tables + indexes are added; no unrelated drift (`memory/process/diff-snapshot-after-ef-tool.md`).
- [ ] **Step 3: Run the EF migration-review gate** — dispatch `.claude/agents/ef-migration-reviewer.md`. Fix any CRITICAL findings (esp. the bool-sentinel trap on `IsActive`). Re-run until clean.
- [ ] **Step 4: Apply + build** — `dotnet build Humans.slnx -v quiet`; optionally `dotnet ef database update` against a scratch DB.
- [ ] **Step 5: Commit** — `git commit -m "feat(infra): migration AddBarrioShiftObligations"`

---

## Chunk 2: Shifts cross-section read surface ⚠️ (Peter approval required)

### Task 2.1: `RotaTargetInfo` projection + `IShiftServiceRead`

**Files:** Create `src/Humans.Application/Interfaces/Shifts/RotaTargetInfo.cs`, `src/Humans.Application/Interfaces/Shifts/IShiftServiceRead.cs`

- [ ] **Step 1: Create `RotaTargetInfo`** (record projection — never an EF entity crosses the boundary)

```csharp
namespace Humans.Application.Interfaces.Shifts;

/// Display + link parts for a Rota obligation target (Shit-ninja). Web builds the URL.
public sealed record RotaTargetInfo(Guid RotaId, string RotaName, Guid TeamId, string TeamSlug);
```

- [ ] **Step 2: Create `IShiftServiceRead`** (model on `ITeamServiceRead`; minimal surface, read-only)

```csharp
namespace Humans.Application.Interfaces.Shifts;

public interface IShiftServiceRead
{
    /// Confirmed signup counts grouped by user, across ALL of the team's rotas, in
    /// the currently-active event (resolved internally). Zero-count users absent.
    Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForTeamAsync(
        Guid teamId, CancellationToken ct = default);

    /// Same, scoped to a single rota (e.g. Shit-ninja within LnT).
    Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForRotaAsync(
        Guid rotaId, CancellationToken ct = default);

    /// Name + link parts for a rota target; null if the rota no longer exists.
    Task<RotaTargetInfo?> GetRotaTargetInfoAsync(Guid rotaId, CancellationToken ct = default);
}
```

- [ ] **Step 3: Build** → success. **Commit** — `git commit -m "feat(shifts): add IShiftServiceRead surface (pending approval)"`

### Task 2.2: Repository reads (TDD — the count query is logic-bearing)

**Files:** Modify `src/Humans.Infrastructure/Repositories/Shifts/ShiftRepository.Management.cs`; add method decls to `src/Humans.Application/Interfaces/Repositories/IShiftManagementRepository.cs`. Test: `tests/Humans.Application.Tests/Shifts/ShiftServiceReadTests.cs`.

- [ ] **Step 1: Write the failing test for the count query** — match the **existing `ShiftRepositoryManagementTests` harness (EF `UseInMemoryDatabase`)**, not a Postgres harness. (The `GroupBy` + nav-traversal query translates fine on InMemory for this shape; the translation risk is low.) Copy that class's setup/attribute style verbatim rather than assuming `[HumansFact]`.

```csharp
[HumansFact]
public async Task TeamCounts_OnlyConfirmed_GroupedByUser_AcrossTeamRotas()
{
    // Arrange: active event; team T with 2 rotas; user A: 2 confirmed + 1 pending; user B: 1 confirmed; other team ignored.
    // Act
    var counts = await repo.GetConfirmedSignupCountsByUserForTeamAsync(teamId, ct);
    // Assert
    counts[userA].Should().Be(2);
    counts[userB].Should().Be(1);
    counts.Should().NotContainKey(userWithOnlyPending);
    counts.Should().NotContainKey(userOnOtherTeam);
}
```

- [ ] **Step 2: Run → fails** (method missing).
- [ ] **Step 3: Add repo method decls** to `IShiftManagementRepository`:

```csharp
Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForTeamAsync(Guid teamId, CancellationToken ct = default);
Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForRotaAsync(Guid rotaId, CancellationToken ct = default);
Task<RotaTargetInfo?> GetRotaTargetInfoAsync(Guid rotaId, CancellationToken ct = default);
```

- [ ] **Step 4: Implement in `ShiftRepository.Management.cs`** (model on `GetConfirmedSignupCountsByShiftAsync`; resolve active event internally)

```csharp
public async Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForTeamAsync(
    Guid teamId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    var activeEventId = await ctx.EventSettings.AsNoTracking()
        .Where(e => e.IsActive).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
    if (activeEventId is null) return new Dictionary<Guid, int>();

    return await ctx.ShiftSignups.AsNoTracking()
        .Where(su => su.Status == SignupStatus.Confirmed
                     && su.Shift.Rota.TeamId == teamId
                     && su.Shift.Rota.EventSettingsId == activeEventId)
        .GroupBy(su => su.UserId)
        .Select(g => new { UserId = g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);
}

public async Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForRotaAsync(
    Guid rotaId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    return await ctx.ShiftSignups.AsNoTracking()
        .Where(su => su.Status == SignupStatus.Confirmed && su.Shift.RotaId == rotaId)
        .GroupBy(su => su.UserId)
        .Select(g => new { UserId = g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);
}

public async Task<RotaTargetInfo?> GetRotaTargetInfoAsync(Guid rotaId, CancellationToken ct = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(ct);
    return await ctx.Rotas.AsNoTracking()
        .Where(r => r.Id == rotaId)
        .Select(r => new RotaTargetInfo(r.Id, r.Name, r.TeamId, r.Team.Slug))
        .FirstOrDefaultAsync(ct);
}
```

> Note: confirm the `Shift.Rota` and `Rota.Team` navs are queryable here (the Shifts aggregate uses them in existing `.Include(...).ThenInclude(s => s.Rota)` joins). If `Rota.Team` nav is stripped, resolve the team slug in the service via `ITeamServiceRead.GetTeamAsync(r.TeamId)` instead and return `TeamSlug` from there.

- [ ] **Step 5: Run the test → passes.** Run the rota-scoped + target-info equivalents (add small tests). **Commit** — `git commit -m "feat(shifts): team/rota confirmed-signup count repo reads"`

### Task 2.3: Implement `IShiftServiceRead` on the service + DI

**Files:** Modify the Shifts management service (`ShiftsShiftManagementService`) to also implement `IShiftServiceRead`; Modify `src/Humans.Web/Extensions/Sections/ShiftsSectionExtensions.cs`.

- [ ] **Step 1: Add `IShiftServiceRead` to the service's interface list** and implement by delegating to the repo (the service has no caching decorator, so no pass-through needed):

```csharp
public async Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByUserForTeamAsync(Guid teamId, CancellationToken ct = default)
    => await _repo.GetConfirmedSignupCountsByUserForTeamAsync(teamId, ct);
// ...rota + target info likewise
```

- [ ] **Step 2: Register in DI** (`ShiftsSectionExtensions`):

```csharp
services.AddScoped<IShiftServiceRead>(sp => sp.GetRequiredService<ShiftsShiftManagementService>());
```

- [ ] **Step 3: Build + existing Shifts tests pass.** **Commit** — `git commit -m "feat(shifts): wire IShiftServiceRead"`

---

## Chunk 3: Camps repository + obligation service

### Task 3.1: `IShiftObligationRepository` + impl

**Files:** Create `src/Humans.Application/Interfaces/Repositories/IShiftObligationRepository.cs`, `src/Humans.Infrastructure/Repositories/Camps/ShiftObligationRepository.cs`

- [ ] **Step 1: Interface** (derive from `IRepository`; owns the two new tables only)

```csharp
public interface IShiftObligationRepository : IRepository
{
    Task<IReadOnlyList<ShiftObligation>> GetAllAsync(CancellationToken ct = default);
    Task<ShiftObligation?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(ShiftObligation obligation, CancellationToken ct = default);
    Task UpdateAsync(ShiftObligation obligation, CancellationToken ct = default);
    Task<IReadOnlyList<CampSeasonShiftObligation>> GetOverridesForYearAsync(int year, CancellationToken ct = default);
    Task SetOverrideAsync(Guid campSeasonId, Guid shiftObligationId, int? requiredShiftCount, CancellationToken ct = default); // null clears
}
```

- [ ] **Step 2: Impl** (model on `CampRepository`: `internal sealed class … : IShiftObligationRepository`, `IDbContextFactory<HumansDbContext>`, `AsNoTracking` reads). `GetOverridesForYearAsync` joins `camp_season_shift_obligations` to `camp_seasons` on `Year`.
- [ ] **Step 3: Register** in `CampsSectionExtensions`: `services.AddSingleton<IShiftObligationRepository, ShiftObligationRepository>();`
- [ ] **Step 4: Build.** **Commit** — `git commit -m "feat(camps): ShiftObligationRepository"`

### Task 3.2: Role-holder-by-slug read on the Camps repo

**Files:** Modify `src/Humans.Application/Interfaces/Repositories/ICampRepository.Roles.cs` + `src/Humans.Infrastructure/Repositories/Camps/CampRepository.Roles.cs`

- [ ] **Step 1: Add a batched read** returning, for a year + role slug, each season's holder UserIds:

```csharp
Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetRoleHolderUserIdsBySlugForYearAsync(
    string roleSlug, int year, CancellationToken ct = default); // key = CampSeasonId
```

- [ ] **Step 2: Implement** (join `CampRoleAssignment → CampRoleDefinition (Slug == roleSlug) → CampMember.UserId`, scoped to seasons of `year`, active members only). Reuse existing role-assignment query shape in `CampRepository.Roles.cs`.
- [ ] **Step 3: Build + a small repo test.** **Commit** — `git commit -m "feat(camps): role-holder-by-slug-for-year read"`

### Task 3.3: `IShiftObligationService` interface + projections

**Files:** Create `src/Humans.Application/Interfaces/Camps/IShiftObligationService.cs`

- [ ] **Step 1: Declare the service + projection records**

```csharp
public interface IShiftObligationService : IApplicationService
{
    Task<BarrioObligationMatrix> GetComplianceMatrixAsync(int year, CancellationToken ct = default);
    Task<BarrioObligationDetail?> GetBarrioObligationDetailAsync(Guid campSeasonId, CancellationToken ct = default);

    Task<IReadOnlyList<ShiftObligationConfigInfo>> GetFunctionsAsync(CancellationToken ct = default);
    Task UpsertFunctionAsync(ShiftObligationConfigInput input, Guid actorUserId, CancellationToken ct = default);
    Task SetOverrideAsync(Guid campSeasonId, Guid shiftObligationId, int? requiredShiftCount, Guid actorUserId, CancellationToken ct = default);

    Task SendReminderAsync(Guid campSeasonId, Guid shiftObligationId, Guid actorUserId, CancellationToken ct = default);
    Task<int> RemindAllNonCompliantAsync(Guid shiftObligationId, Guid actorUserId, CancellationToken ct = default); // returns count emailed
}

public sealed record BarrioObligationMatrix(
    int Year,
    IReadOnlyList<ObligationColumn> Columns,
    IReadOnlyList<BarrioRow> Rows,
    IReadOnlyList<ExemptBarrio> ExemptNobodiesOrg,         // Norg
    IReadOnlyList<OffGridBarrio> OffGridForPower);          // OwnSupply / unclassified, per grid function

public sealed record ObligationColumn(Guid ShiftObligationId, string Name, string TargetUrl, ObligationApplicability Applicability);
public sealed record BarrioRow(Guid CampSeasonId, string BarrioName, string Slug, int ActiveMemberCount, IReadOnlyList<ObligationCell> Cells);
public sealed record ObligationCell(Guid ShiftObligationId, bool Applicable, int Done, int Required, bool UnderMembered);
public sealed record ExemptBarrio(Guid CampSeasonId, string BarrioName, int ActiveMemberCount);
public sealed record OffGridBarrio(Guid CampSeasonId, string BarrioName, string Reason); // "OwnSupply" | "Unclassified"

public sealed record BarrioObligationDetail(
    Guid CampSeasonId, string BarrioName,
    IReadOnlyList<ObligationDetailFunction> Functions);
public sealed record ObligationDetailFunction(
    Guid ShiftObligationId, string Name, int Done, int Required,
    IReadOnlyList<SignedUpMember> SignedUp,                 // count desc
    IReadOnlyList<string> NotYetSignedUpNames);            // optional chase list

public sealed record SignedUpMember(Guid UserId, string Name, int Count);
public sealed record ShiftObligationConfigInfo(Guid Id, ShiftObligationTargetType TargetType, Guid TargetId, string TargetName, string CampRoleSlug, ObligationApplicability Applicability, int DefaultRequiredShiftCount, bool IsActive, int SortOrder);
public sealed record ShiftObligationConfigInput(Guid? Id, ShiftObligationTargetType TargetType, Guid TargetId, string CampRoleSlug, ObligationApplicability Applicability, int DefaultRequiredShiftCount, bool IsActive, int SortOrder);
```

- [ ] **Step 2: Build.** **Commit** — `git commit -m "feat(camps): IShiftObligationService interface + projections"`

### Task 3.4: Compliance computation (TDD — core logic)

**Files:** Create `src/Humans.Application/Services/Camps/ShiftObligationService.cs`. Test: `tests/Humans.Application.Tests/Camps/ShiftObligationServiceTests.cs`. Deps (ctor): `IShiftObligationRepository`, `ICampServiceRead`, `ICampRepository`, `IShiftServiceRead`, `ITeamServiceRead`, `IUserService`, `IEmailService`, `IEmailMessageFactory`, `IAuditLogService`, `IClock`, `ILogger<>`.

> The two filtering layers + override + under-membered + per-function target selection are the regression-prone heart. Write these tests first.

- [ ] **Step 1: Failing test — global Norg exemption + per-function applicability**

```csharp
[HumansFact]
public async Task Matrix_ExemptsNorg_AndAppliesPowerGridFilter()
{
    // barrios: Yellow(owes power), OwnSupply(power n/a), Norg(exempt), unset(power n/a)
    // functions: Power(ElectricalGridConnected, Team T), ShitNinja(AllBarrios, Rota R)
    var m = await sut.GetComplianceMatrixAsync(2026, ct);

    m.ExemptNobodiesOrg.Select(e => e.BarrioName).Should().ContainSingle().Which.Should().Be("Norg Camp");
    var yellow = m.Rows.Single(r => r.BarrioName == "Yellow Camp");
    yellow.Cells.Single(c => c.ShiftObligationId == powerId).Applicable.Should().BeTrue();
    var ownSupply = m.Rows.Single(r => r.BarrioName == "OwnSupply Camp");
    ownSupply.Cells.Single(c => c.ShiftObligationId == powerId).Applicable.Should().BeFalse();
    ownSupply.Cells.Single(c => c.ShiftObligationId == shitNinjaId).Applicable.Should().BeTrue(); // all barrios
    m.Rows.Should().NotContain(r => r.BarrioName == "Norg Camp"); // exempt removed from matrix
    m.OffGridForPower.Should().Contain(o => o.BarrioName == "OwnSupply Camp" && o.Reason == "OwnSupply");
}
```

- [ ] **Step 2: Failing test — done sums only this barrio's active members; override beats default; under-membered**

```csharp
[HumansFact]
public async Task Cell_Done_SumsBarrioMembers_OverrideBeatsDefault_UnderMemberedFlag()
{
    // Power default required 6; override 8 for "Yellow Camp"; members A(2 signups)+B(1); only 2 active members
    var m = await sut.GetComplianceMatrixAsync(2026, ct);
    var cell = m.Rows.Single(r => r.BarrioName == "Yellow Camp").Cells.Single(c => c.ShiftObligationId == powerId);
    cell.Done.Should().Be(3);           // 2 + 1, ignoring non-members signed up
    cell.Required.Should().Be(8);       // override
    cell.UnderMembered.Should().BeTrue(); // 2 active members < 8
}
```

- [ ] **Step 3: Run → fail.**
- [ ] **Step 4: Implement `GetComplianceMatrixAsync`** — fetch barrios via `ICampServiceRead.GetCampsForYearAsync(year)` (members + leads + ElectricalGrid). **Verify** that path returns `Members` populated; if not, read seasons via `ICampRepository` (same section). For each active function: pick count map by `TargetType` (`IShiftServiceRead` team vs rota); resolve column name/URL (`ITeamServiceRead.GetTeamAsync` for Team, `IShiftServiceRead.GetRotaTargetInfoAsync` for Rota). Apply: (2a) drop `ElectricalGrid == Norg` to `ExemptNobodiesOrg`; (2b) for `ElectricalGridConnected` functions mark cell `Applicable=false` when grid ∈ `{OwnSupply, Unknown, null}` and add to `OffGridForPower`. `Done = Σ counts[memberUserId]`; `Required = override ?? default`; `UnderMembered = activeMembers < Required`.
- [ ] **Step 5: Run → pass.** **Commit** — `git commit -m "feat(camps): obligation compliance matrix"`

### Task 3.5: Per-barrio detail (TDD)

**Files:** same service + test file.

- [ ] **Step 1: Failing test** — detail lists this barrio's signed-up members (name+count desc), omits non-members, exempt/n-a functions absent.
- [ ] **Step 2: Implement `GetBarrioObligationDetailAsync`** — reuse the same count maps; intersect with barrio active members; resolve names via `IUserService.GetByIdsAsync`; sort desc; build `NotYetSignedUpNames` from members with no entry.
- [ ] **Step 3: Run → pass. Commit** — `git commit -m "feat(camps): per-barrio obligation detail"`

### Task 3.6: Config CRUD + override + DI

**Files:** same service; `CampsSectionExtensions`.

- [ ] **Step 1: Implement** `GetFunctionsAsync` (resolve `TargetName` via Team/Rota reads), `UpsertFunctionAsync`, `SetOverrideAsync` (delegate to repo; write audit on config change).
- [ ] **Step 2: Register the service** (no caching decorator): `services.AddScoped<IShiftObligationService, ShiftObligationService>();` — **Scoped is required** (not Singleton): it depends on scoped reads (`IShiftServiceRead`, `IUserService`, `ICampServiceRead`), so a Singleton would be a captive-dependency bug. The repo (Task 3.1) is Singleton because it's stateless over `IDbContextFactory`, per Camps convention.
- [ ] **Step 3: Build + tests pass. Commit** — `git commit -m "feat(camps): obligation config CRUD + DI"`

---

## Chunk 4: Email + audit

### Task 4.1: AuditAction value

**Files:** Modify `src/Humans.Domain/Enums/AuditAction.cs`

- [ ] **Step 1: Append `BarrioShiftReminderSent`** to the enum tail (append-only). **Build. Commit.**

### Task 4.2: Reminder email (renderer + factory + resx) (TDD on recipients)

**Files:** Modify `IEmailRenderer` + `EmailRenderer.cs`, `IEmailMessageFactory.cs` + `EmailMessageFactory.cs`, `Properties/SharedResource.resx` (+ `.es.resx`). Test: recipient resolution in `ShiftObligationServiceTests`.

- [ ] **Step 1: Add resx keys** `Email_BarrioShiftReminder_Subject`, `Email_BarrioShiftReminder_Body` (EN + ES) — body args: barrio, function, done, required, link.
- [ ] **Step 2: Add `RenderBarrioShiftObligationReminder(...)`** to `IEmailRenderer` + `EmailRenderer` (model on `RenderCoordinatorRotaMessage`: `RenderLocalized` + `Lf`/`L`, HTML-encode args).
- [ ] **Step 3: Add factory method** `BarrioShiftObligationReminder(...)` → `new EmailMessage(recipientEmail, recipientName, content.Subject, content.HtmlBody, "barrio_shift_obligation_reminder", MessageCategory.VolunteerUpdates, ReplyTo: functionReplyTo)`.
- [ ] **Step 4: Failing test — `SendReminderAsync` recipients** = leads ∪ role-holder; role-holder absent → leads only; one `IEmailService.SendAsync` per recipient; one `BarrioShiftReminderSent` audit entry.
- [ ] **Step 5: Implement `SendReminderAsync` / `RemindAllNonCompliantAsync`** — resolve recipient UserIds (season `LeadUserIds` from `ICampServiceRead` ∪ `ICampRepository.GetRoleHolderUserIdsBySlugForYearAsync(fn.CampRoleSlug, year)[campSeasonId]`), `IUserService.GetByIdsAsync` → emails, build + send per recipient, audit once. Bulk = iterate non-compliant barrios for the function.
- [ ] **Step 6: Run → pass. Commit** — `git commit -m "feat(camps): barrio shift reminder emails"`

---

## Chunk 5: Web (admin matrix, detail, functions, barrio-lead view)

### Task 5.1: View models

**Files:** Create `src/Humans.Web/Models/CampAdmin/ShiftObligationViewModels.cs` (matrix, detail, functions VMs — map from the service projections; model on `CampRoleComplianceViewModel`).
- [ ] Build. Commit.

### Task 5.2: Admin matrix + detail + actions

**Files:** Modify `src/Humans.Web/Controllers/CampAdminController.cs`; create `Views/CampAdmin/ShiftObligations.cshtml`, `ShiftObligationDetail.cshtml`. Gate all with `[Authorize(Policy = PolicyNames.CampAdminOrAdmin)]`.

- [ ] **Step 1: Actions** — `GET ShiftObligations(int? year)`, `GET ShiftObligations/{campSeasonId}` (detail), `POST Remind`, `POST RemindAllNonCompliant`, `POST SetOverride`. Controllers parse/format only; call `IShiftObligationService`; `TempData` for success/error; redirect back.
- [ ] **Step 2: Views** — matrix (rows×cols, met/unmet/under-membered/n-a styling per the lofi mock; cell links to detail; column header → `TargetUrl`; "remind all unmet"; inline override `SetOverride`; two below-matrix groups: exempt Norg, off-grid-for-power). Detail view: per function done/required + signed-up members (name + count) + optional "not yet" tail.
- [ ] **Step 3: Build + manual smoke** (`@superpowers:verification-before-completion` — run the app, hit `/Camps/Admin/ShiftObligations`). **Commit.**

### Task 5.3: Functions config page

**Files:** `CampAdminController` (`GET/POST ShiftObligations/Functions`), `Views/CampAdmin/ShiftObligationFunctions.cshtml`.
- [ ] CRUD form (target type → Team/Rota picker, camp-role slug, applicability, default count, active, sort). Build + smoke. Commit.

### Task 5.4: Barrio-lead scoped detail

**Files:** Modify `src/Humans.Web/Controllers/CampController.cs`; create `Views/Camp/ShiftObligations.cshtml`; add a link in `Views/Camp/Members.cshtml`.

- [ ] **Step 1: Action `GET /Camps/{slug}/ShiftObligations`** — resolve camp by slug via `ICampServiceRead.GetCampBySlugAsync`; **authorize**: `season.IsLead(currentUserId)` OR CampAdmin/Admin, else `Forbid()` (mirror the `/Camps/{slug}/Edit` lead gate). Call `GetBarrioObligationDetailAsync(currentSeasonId)`. Render the read-only detail (no override/remind).
- [ ] **Step 2: Failing controller/auth test** — lead of the barrio allowed; non-lead non-admin denied; lead of a *different* barrio denied. Implement to pass.
- [ ] **Step 3: Add lead link** from `Views/Camp/Members.cshtml`. Build + smoke as a lead. **Commit.**

### Task 5.5: Admin nav link + nav-completeness

**Files:** Modify `Views/CampAdmin/Index.cshtml` (link to ShiftObligations).
- [ ] Add the link; run `/nav-audit` (or the nav-completeness check). Commit.

---

## Chunk 6: Architecture test + final verification

### Task 6.1: Table-ownership architecture test

**Files:** Create `tests/Humans.Application.Tests/Architecture/ShiftObligationArchitectureTests.cs` (model on existing Architecture tests).
- [ ] Assert `shift_obligations` + `camp_season_shift_obligations` are owned only by `ShiftObligationRepository`; `Camps → Shifts` access is via `IShiftServiceRead` only (no direct Shifts repo/`IShiftManagementService` ref from Camps). Run → pass. Commit.

### Task 6.2: Full suite + requesting code review

- [ ] `dotnet build Humans.slnx -v quiet` && `dotnet test Humans.slnx -v quiet` → all green (evidence required; `@superpowers:verification-before-completion`).
- [ ] `@superpowers:requesting-code-review` over the branch diff; address findings.
- [ ] Open PR to `origin/main` (see Git Workflow in CLAUDE.md). Flag the **`IShiftServiceRead` approval** + the **deferred `Orange` enum** in the PR body.

---

## Notes / deferred (from spec)

- **Orange grid:** no `ElectricalGrid.Orange` value today; the exclusion-based Power rule absorbs it automatically if added later (Camps/domain change). Out of scope here.
- **Per-shift detail** (which days each member signed up) is out of scope — the detail view shows per-function counts. A richer Shifts read would be needed.
- **No new caching**, **no new GDPR contributor** (config + season-keyed counts, no new PII table), **no `IUserMerge`** (no per-UserId rows in the new tables).
- **Water-truck / Auger** dropped (not shifts).
