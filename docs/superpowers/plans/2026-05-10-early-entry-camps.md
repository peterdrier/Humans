# Early Entry (camps-only v1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Track per-camp Early Entry slot allocations and per-member EE grants inside the Camps section, with full audit trail and authz, without introducing any new tables or a new section.

**Architecture:** Three additive field changes (`CampSeason.EeSlotCount`, `CampMember.HasEarlyEntry`, `CampSettings.EeStartDate`); three new methods on `ICampService` (51→54 with Peter's explicit sign-off, logged in the ratchet); UI on existing `/Camps/Admin` and `/Camps/{slug}/Edit/Members`; member-removal paths gain a one-line cascade to clear `HasEarlyEntry`. No public view exposes EE.

**Tech Stack:** ASP.NET Core, EF Core, Postgres, NodaTime, xUnit + AwesomeAssertions + NSubstitute, MVC + Razor.

**Spec:** [`docs/superpowers/specs/2026-05-10-early-entry-camps-design.md`](../specs/2026-05-10-early-entry-camps-design.md)
**Issue:** [nobodies-collective/Humans#490](https://github.com/nobodies-collective/Humans/issues/490)
**Branch:** `issue-490-early-entry`

---

## Phase 1 — Domain + EF + Migration

### Task 1: Add EE fields to entities + EF configurations

**Files:**
- Modify: `src/Humans.Domain/Entities/CampSeason.cs`
- Modify: `src/Humans.Domain/Entities/CampMember.cs`
- Modify: `src/Humans.Domain/Entities/CampSettings.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Camps/CampSeasonConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Camps/CampMemberConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/Camps/CampSettingsConfiguration.cs`

- [ ] **Step 1: Add `EeSlotCount` to `CampSeason`**

In `CampSeason.cs`, add to the existing property list:

```csharp
/// <summary>
/// Number of Early Entry slots this season's camp may grant to its members.
/// CampAdmin-managed. 0 = no EE this season. See docs/sections/Camps.md.
/// </summary>
public int EeSlotCount { get; set; }
```

- [ ] **Step 2: Add `HasEarlyEntry` to `CampMember`**

In `CampMember.cs`, add after `RemovedByUserId`:

```csharp
/// <summary>
/// True when this member holds an Early Entry grant for the season's camp.
/// Granted by camp leads / CampAdmin; cleared on member removal. Never rendered
/// on anonymous/public views.
/// </summary>
public bool HasEarlyEntry { get; set; }
```

- [ ] **Step 3: Add `EeStartDate` to `CampSettings`**

Replace `CampSettings.cs` contents with:

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class CampSettings
{
    public Guid Id { get; init; }
    public int PublicYear { get; set; }
    public List<int> OpenSeasons { get; set; } = new();

    /// <summary>
    /// Date from which humans holding an EE grant for the current public year
    /// may enter the site. Set by CampAdmin; nullable until configured for the
    /// year. v1 has no consumer beyond informational display on /Camps/Admin —
    /// a future gate endpoint will read this.
    /// </summary>
    public LocalDate? EeStartDate { get; set; }
}
```

- [ ] **Step 4: Update `CampSeasonConfiguration`**

Open `CampSeasonConfiguration.cs`. Inside `Configure(EntityTypeBuilder<CampSeason> builder)`, add (next to other scalar property mappings):

```csharp
builder.Property(s => s.EeSlotCount)
    .IsRequired()
    .HasDefaultValue(0);
```

- [ ] **Step 5: Update `CampMemberConfiguration`**

Open `CampMemberConfiguration.cs`. Inside `Configure(...)`, add:

```csharp
builder.Property(m => m.HasEarlyEntry)
    .IsRequired()
    .HasDefaultValue(false);
```

- [ ] **Step 6: Update `CampSettingsConfiguration`**

Open `CampSettingsConfiguration.cs`. Inside `Configure(...)`, add:

```csharp
builder.Property(s => s.EeStartDate); // nullable LocalDate; default conversion via Npgsql NodaTime
```

- [ ] **Step 7: Build to confirm no compile errors**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds with no errors.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Domain/Entities/CampSeason.cs \
        src/Humans.Domain/Entities/CampMember.cs \
        src/Humans.Domain/Entities/CampSettings.cs \
        src/Humans.Infrastructure/Data/Configurations/Camps/CampSeasonConfiguration.cs \
        src/Humans.Infrastructure/Data/Configurations/Camps/CampMemberConfiguration.cs \
        src/Humans.Infrastructure/Data/Configurations/Camps/CampSettingsConfiguration.cs
git commit -m "issue-490: add EE fields to CampSeason, CampMember, CampSettings"
```

### Task 2: EF migration for the new columns

**Files:**
- Create: `src/Humans.Infrastructure/Migrations/<timestamp>_AddEarlyEntryFields.cs` (EF-generated)

- [ ] **Step 1: Generate the migration**

Run from repo root:

```bash
dotnet ef migrations add AddEarlyEntryFields \
    --project src/Humans.Infrastructure \
    --startup-project src/Humans.Web \
    -- --environment Development
```

Expected: produces a new migration file under `src/Humans.Infrastructure/Migrations/`.

- [ ] **Step 2: Read the generated migration and confirm it only adds three columns**

The migration should `Up` add:
- `ee_slot_count` (int, NOT NULL, default 0) to `camp_seasons`
- `has_early_entry` (boolean, NOT NULL, default false) to `camp_members`
- `ee_start_date` (date, NULL) to `camp_settings`

`Down` should drop those three columns. **Do not hand-edit the migration** per `memory/process/no-hand-edited-migrations.md`. If it generates anything else, fix the entity/config and regenerate.

- [ ] **Step 3: Build + run database tests to confirm migration is clean**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Migration"`
Expected: passes. If no migration tests exist, just run a build.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "issue-490: add EF migration AddEarlyEntryFields"
```

---

## Phase 2 — Audit + Service Surface

### Task 3: Add 4 new AuditAction enum values

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs`

- [ ] **Step 1: Append new values**

In `AuditAction.cs`, append at the end of the enum body (preserve ordering — the enum is positional and existing values must not move):

```csharp
    CampEarlyEntryGranted,
    CampEarlyEntryRevoked,
    CampSeasonEeSlotCountChanged,
    CampSettingsEeStartDateChanged,
```

- [ ] **Step 2: Build to confirm no compile errors**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/AuditAction.cs
git commit -m "issue-490: add EE audit actions"
```

### Task 4: Add ICampService method signatures + raise budget 51→54

**Files:**
- Modify: `src/Humans.Application/Interfaces/Camps/ICampService.cs`
- Modify: `tests/Humans.Application.Tests/Architecture/InterfaceMethodBudgetTests.cs`
- Modify: `memory/architecture/interface-method-budget-ratchet.md`

- [ ] **Step 1: Add 3 method signatures to `ICampService`**

In `ICampService.cs`, append a new region (or place after the existing setting/season mutation methods such as `SetNameLockDateAsync`):

```csharp
/// <summary>
/// Sets the global Early Entry start date in CampSettings. CampAdmin/Admin only;
/// authorization enforced at the controller layer.
/// </summary>
Task SetEeStartDateAsync(LocalDate? eeStartDate, CancellationToken cancellationToken = default);

/// <summary>
/// Sets the EE slot cap for a given camp season. CampAdmin/Admin only.
/// Allowed to drop below the current granted-count: existing grants are retained
/// but no new grants can be issued until the granted-count falls back under the cap.
/// </summary>
Task SetCampSeasonEeSlotCountAsync(
    Guid campSeasonId, int slotCount, Guid actorUserId,
    CancellationToken cancellationToken = default);

/// <summary>
/// Grants or revokes Early Entry for a CampMember. Camp lead, CoLead, CampAdmin,
/// or Admin only; authorization enforced at the controller layer.
/// Rejects when granting would push the season's active-granted count above
/// CampSeason.EeSlotCount, or when the member is not Status=Active.
/// Idempotent: writes no audit row when the value is already at the requested state.
/// </summary>
Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
    Guid campMemberId, bool granted, Guid actorUserId,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 2: Create the outcome enum**

Create file `src/Humans.Application/Services/Camps/SetEarlyEntryOutcome.cs`:

```csharp
namespace Humans.Application.Services.Camps;

public enum SetEarlyEntryOutcome
{
    Success,
    NoChange,
    SlotCapExceeded,
    MemberNotActive,
    MemberNotFound,
}
```

- [ ] **Step 3: Bump the budget ceiling and log the exception**

In `tests/Humans.Application.Tests/Architecture/InterfaceMethodBudgetTests.cs`, change the `ICampService` entry from `51` to `54`. Add a comment line above the entry:

```csharp
        // 51→54: issue-490 — Early Entry (camps consumer). Added
        // SetEeStartDateAsync, SetCampSeasonEeSlotCountAsync, SetEarlyEntryAsync.
        // Authorized by Peter 2026-05-10. EE state lives on CampSeason/CampMember/
        // CampSettings — tables ICampService already owns — so the methods belong
        // here per design-rules §6 (no service split).
        [typeof(ICampService)] = 54,
```

In `memory/architecture/interface-method-budget-ratchet.md`, append to the **Authorized exceptions log** section:

```markdown
- 2026-05-10 (issue #490, Peter explicit sign-off): +3 on `ICampService` for the Early Entry camps consumer: `SetEeStartDateAsync`, `SetCampSeasonEeSlotCountAsync`, `SetEarlyEntryAsync`. EE state attaches to `CampSeason`/`CampMember`/`CampSettings`, all ICampService-owned tables, so the methods belong here per design-rules §6.
```

- [ ] **Step 4: Build to confirm interface signatures compile (impl placeholders coming next)**

The existing `CampService` won't compile yet because it doesn't implement the new methods. Add stub impls in `CampService.cs` at the bottom of the class (just enough to compile — real impls come in Phase 3):

```csharp
public Task SetEeStartDateAsync(LocalDate? eeStartDate, CancellationToken cancellationToken = default)
    => throw new NotImplementedException();

public Task SetCampSeasonEeSlotCountAsync(
    Guid campSeasonId, int slotCount, Guid actorUserId,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException();

public Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
    Guid campMemberId, bool granted, Guid actorUserId,
    CancellationToken cancellationToken = default)
    => throw new NotImplementedException();
```

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds.

- [ ] **Step 5: Run the budget test to confirm 54 == count**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~InterfaceMethodBudgetTests"`
Expected: both `Interface_method_count_does_not_exceed_budget` and `Budgets_are_tight_and_not_padded` pass.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Camps/ICampService.cs \
        src/Humans.Application/Services/Camps/SetEarlyEntryOutcome.cs \
        src/Humans.Application/Services/Camps/CampService.cs \
        tests/Humans.Application.Tests/Architecture/InterfaceMethodBudgetTests.cs \
        memory/architecture/interface-method-budget-ratchet.md
git commit -m "issue-490: ICampService EE surface (+3, budget 51→54 logged)"
```

---

## Phase 3 — CampService implementation (TDD)

Test file used throughout Phase 3: `tests/Humans.Application.Tests/Services/CampServiceTests.cs` (existing). Reuse its fixture pattern — see lines 25–82 — but **place new tests in a new dedicated file** `tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs` to keep the diff readable and the existing file's grouping intact. Copy the constructor pattern verbatim (in-memory DbContext, `FakeClock(Instant.FromUtc(2026, 3, 13, 12, 0))`, `IAuditLogService` substitute, etc.).

### Task 5: `SetEeStartDateAsync` (simplest, no dependencies)

**Files:**
- Create: `tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs`
- Modify: `src/Humans.Application/Services/Camps/CampService.cs`
- Modify: `src/Humans.Application/Interfaces/Repositories/ICampRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs`

- [ ] **Step 1: Write the failing test**

Create `CampServiceEarlyEntryTests.cs`. Use the constructor from `CampServiceTests.cs` (lines 25–82) — copy it verbatim. Add the first test:

```csharp
[HumansFact]
public async Task SetEeStartDateAsync_SetsValue_AndInvalidatesSettingsCache()
{
    await SeedSettingsAsync(); // helper from CampServiceTests style
    var date = new LocalDate(2026, 8, 7);

    await _service.SetEeStartDateAsync(date);

    var settings = await _service.GetSettingsAsync();
    settings.EeStartDate.Should().Be(date);

    await _auditLog.Received(1).LogAsync(
        AuditAction.CampSettingsEeStartDateChanged,
        nameof(CampSettings), Arg.Any<Guid>(),
        Arg.Any<string>(), Arg.Any<Guid?>(),
        Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}
```

`SeedSettingsAsync` already exists on `CampServiceTests` — copy its body into this file (it inserts a `CampSettings` singleton).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetEeStartDateAsync_SetsValue"`
Expected: FAIL with `NotImplementedException`.

- [ ] **Step 3: Add repository method**

In `ICampRepository.cs`, add:

```csharp
Task SetEeStartDateAsync(LocalDate? eeStartDate, CancellationToken cancellationToken = default);
```

In `CampRepository.cs`, implement (model after `SetPublicYearAsync`):

```csharp
public async Task SetEeStartDateAsync(
    LocalDate? eeStartDate, CancellationToken cancellationToken = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
    var settings = await ctx.CampSettings.FirstAsync(cancellationToken);
    settings.EeStartDate = eeStartDate;
    await ctx.SaveChangesAsync(cancellationToken);
}
```

- [ ] **Step 4: Implement service method**

Replace the `NotImplementedException` stub for `SetEeStartDateAsync` with:

```csharp
public async Task SetEeStartDateAsync(
    LocalDate? eeStartDate, CancellationToken cancellationToken = default)
{
    await _repo.SetEeStartDateAsync(eeStartDate, cancellationToken);
    _cache.InvalidateCampSettings();

    var settings = await _repo.GetSettingsAsync(cancellationToken);
    await _auditLog.LogAsync(
        AuditAction.CampSettingsEeStartDateChanged,
        nameof(CampSettings), settings.Id,
        eeStartDate is null
            ? "EE start date cleared."
            : $"EE start date set to {eeStartDate.Value:yyyy-MM-dd}.",
        actorUserId: null);
}
```

If `IAuditLogService.LogAsync` requires a non-null actor, capture the actor at the controller layer instead — but for now we don't know whether the audit signature requires it. Verify against `CampService.SetPublicYearAsync` callsites in tests — if `SetPublicYearAsync` doesn't audit, ours doesn't need to either. *Decision: keep the audit call here; pass `null` for actor (audit table allows null actor for system-driven changes per existing usage; if the test fails with a non-null constraint, adjust the service to accept an `actorUserId` parameter on `SetEeStartDateAsync` — note this changes the interface signature.)*

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetEeStartDateAsync_SetsValue"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Services/Camps/CampService.cs \
        src/Humans.Application/Interfaces/Repositories/ICampRepository.cs \
        src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs \
        tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs
git commit -m "issue-490: implement SetEeStartDateAsync with audit"
```

### Task 6: `SetCampSeasonEeSlotCountAsync`

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs`
- Modify: `src/Humans.Application/Services/Camps/CampService.cs`
- Modify: `src/Humans.Application/Interfaces/Repositories/ICampRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs`

- [ ] **Step 1: Write failing tests (happy path + reduce-below-current allowed)**

Append to `CampServiceEarlyEntryTests.cs`:

```csharp
[HumansFact]
public async Task SetCampSeasonEeSlotCountAsync_SetsValue_AndAuditsChange()
{
    await SeedSettingsAsync();
    var (camp, season) = await SeedCampWithSeasonAsync(); // helper, see step 2
    var actor = Guid.NewGuid();

    await _service.SetCampSeasonEeSlotCountAsync(season.Id, 13, actor);

    var reloaded = await _dbContext.CampSeasons.FirstAsync(s => s.Id == season.Id);
    reloaded.EeSlotCount.Should().Be(13);

    await _auditLog.Received(1).LogAsync(
        AuditAction.CampSeasonEeSlotCountChanged,
        nameof(CampSeason), season.Id,
        Arg.Any<string>(), actor,
        camp.Id, nameof(Camp), Arg.Any<CancellationToken>());
}

[HumansFact]
public async Task SetCampSeasonEeSlotCountAsync_AllowsReducingBelowCurrentGrants()
{
    await SeedSettingsAsync();
    var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 10);
    // Seed 5 active members all with HasEarlyEntry=true.
    for (var i = 0; i < 5; i++)
        await SeedActiveMemberWithEarlyEntryAsync(season.Id);

    var actor = Guid.NewGuid();
    await _service.SetCampSeasonEeSlotCountAsync(season.Id, 3, actor);

    var reloaded = await _dbContext.CampSeasons.FirstAsync(s => s.Id == season.Id);
    reloaded.EeSlotCount.Should().Be(3);

    // Existing grants persist — no auto-revoke.
    var grantedCount = await _dbContext.CampMembers
        .CountAsync(m => m.CampSeasonId == season.Id
                      && m.HasEarlyEntry
                      && m.Status == CampMemberStatus.Active);
    grantedCount.Should().Be(5);
}
```

Add helper methods at the bottom of the test class:

```csharp
private async Task<(Camp camp, CampSeason season)> SeedCampWithSeasonAsync(int initialEeSlotCount = 0)
{
    var camp = new Camp
    {
        Id = Guid.NewGuid(),
        Slug = $"camp-{Guid.NewGuid():N}".Substring(0, 12),
        // ...fill required fields per CampServiceTests seed helpers
    };
    var season = new CampSeason
    {
        Id = Guid.NewGuid(),
        CampId = camp.Id,
        Year = 2026,
        Status = CampSeasonStatus.Active,
        Name = "Test Camp",
        EeSlotCount = initialEeSlotCount,
    };
    _dbContext.Camps.Add(camp);
    _dbContext.CampSeasons.Add(season);
    await _dbContext.SaveChangesAsync();
    return (camp, season);
}

private async Task<CampMember> SeedActiveMemberWithEarlyEntryAsync(Guid campSeasonId)
{
    var member = new CampMember
    {
        Id = Guid.NewGuid(),
        CampSeasonId = campSeasonId,
        UserId = Guid.NewGuid(),
        Status = CampMemberStatus.Active,
        RequestedAt = _clock.GetCurrentInstant(),
        ConfirmedAt = _clock.GetCurrentInstant(),
        HasEarlyEntry = true,
    };
    _dbContext.CampMembers.Add(member);
    await _dbContext.SaveChangesAsync();
    return member;
}
```

Note: `SeedCampWithSeasonAsync` needs every non-null property on Camp/CampSeason. Crib the field list from `CampServiceTests.MakeSeasonData()` and adjust.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetCampSeasonEeSlotCountAsync"`
Expected: FAIL with `NotImplementedException`.

- [ ] **Step 3: Add repository method**

In `ICampRepository.cs`:

```csharp
Task<(int OldValue, int NewValue, Guid CampId)?> SetCampSeasonEeSlotCountAsync(
    Guid campSeasonId, int slotCount, CancellationToken cancellationToken = default);
```

In `CampRepository.cs`:

```csharp
public async Task<(int OldValue, int NewValue, Guid CampId)?> SetCampSeasonEeSlotCountAsync(
    Guid campSeasonId, int slotCount, CancellationToken cancellationToken = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
    var season = await ctx.CampSeasons
        .FirstOrDefaultAsync(s => s.Id == campSeasonId, cancellationToken);
    if (season is null) return null;

    var oldValue = season.EeSlotCount;
    if (oldValue == slotCount) return (oldValue, slotCount, season.CampId);

    season.EeSlotCount = slotCount;
    await ctx.SaveChangesAsync(cancellationToken);
    return (oldValue, slotCount, season.CampId);
}
```

- [ ] **Step 4: Implement service method**

Replace the stub in `CampService.cs`:

```csharp
public async Task SetCampSeasonEeSlotCountAsync(
    Guid campSeasonId, int slotCount, Guid actorUserId,
    CancellationToken cancellationToken = default)
{
    if (slotCount < 0)
        throw new ArgumentOutOfRangeException(nameof(slotCount), "EE slot count cannot be negative.");

    var result = await _repo.SetCampSeasonEeSlotCountAsync(campSeasonId, slotCount, cancellationToken);
    if (result is null)
        throw new InvalidOperationException("Camp season not found.");

    var (oldValue, newValue, campId) = result.Value;
    if (oldValue == newValue) return;

    await _auditLog.LogAsync(
        AuditAction.CampSeasonEeSlotCountChanged,
        nameof(CampSeason), campSeasonId,
        $"EE slot count changed from {oldValue} to {newValue}.",
        actorUserId,
        relatedEntityId: campId, relatedEntityType: nameof(Camp),
        cancellationToken: cancellationToken);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetCampSeasonEeSlotCountAsync"`
Expected: PASS (both tests).

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Services/Camps/CampService.cs \
        src/Humans.Application/Interfaces/Repositories/ICampRepository.cs \
        src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs \
        tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs
git commit -m "issue-490: implement SetCampSeasonEeSlotCountAsync"
```

### Task 7: `SetEarlyEntryAsync` happy path (grant + revoke)

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs`
- Modify: `src/Humans.Application/Services/Camps/CampService.cs`
- Modify: `src/Humans.Application/Interfaces/Repositories/ICampRepository.cs`
- Modify: `src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs`

- [ ] **Step 1: Write failing tests**

Append:

```csharp
[HumansFact]
public async Task SetEarlyEntryAsync_Grant_SetsFlagAndAudits()
{
    await SeedSettingsAsync();
    var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
    var member = await SeedActiveMemberAsync(season.Id);
    var actor = Guid.NewGuid();

    var outcome = await _service.SetEarlyEntryAsync(member.Id, granted: true, actor);

    outcome.Should().Be(SetEarlyEntryOutcome.Success);
    var reloaded = await _dbContext.CampMembers.FirstAsync(m => m.Id == member.Id);
    reloaded.HasEarlyEntry.Should().BeTrue();

    await _auditLog.Received(1).LogAsync(
        AuditAction.CampEarlyEntryGranted,
        nameof(CampMember), member.Id,
        Arg.Any<string>(), actor,
        camp.Id, nameof(Camp), Arg.Any<CancellationToken>());
}

[HumansFact]
public async Task SetEarlyEntryAsync_Revoke_ClearsFlagAndAudits()
{
    await SeedSettingsAsync();
    var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
    var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);
    var actor = Guid.NewGuid();

    var outcome = await _service.SetEarlyEntryAsync(member.Id, granted: false, actor);

    outcome.Should().Be(SetEarlyEntryOutcome.Success);
    var reloaded = await _dbContext.CampMembers.FirstAsync(m => m.Id == member.Id);
    reloaded.HasEarlyEntry.Should().BeFalse();

    await _auditLog.Received(1).LogAsync(
        AuditAction.CampEarlyEntryRevoked,
        nameof(CampMember), member.Id,
        Arg.Any<string>(), actor,
        camp.Id, nameof(Camp), Arg.Any<CancellationToken>());
}
```

Add helper:

```csharp
private async Task<CampMember> SeedActiveMemberAsync(Guid campSeasonId)
{
    var member = new CampMember
    {
        Id = Guid.NewGuid(),
        CampSeasonId = campSeasonId,
        UserId = Guid.NewGuid(),
        Status = CampMemberStatus.Active,
        RequestedAt = _clock.GetCurrentInstant(),
        ConfirmedAt = _clock.GetCurrentInstant(),
        HasEarlyEntry = false,
    };
    _dbContext.CampMembers.Add(member);
    await _dbContext.SaveChangesAsync();
    return member;
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetEarlyEntryAsync_Grant|FullyQualifiedName~SetEarlyEntryAsync_Revoke"`
Expected: FAIL with `NotImplementedException`.

- [ ] **Step 3: Add repository helper**

In `ICampRepository.cs`:

```csharp
/// <summary>
/// Loads the CampMember with its CampSeason (needed for season + camp navigation
/// when computing overflow checks and writing audit context). Returns null when
/// the member does not exist.
/// </summary>
Task<CampMember?> GetMemberWithSeasonAsync(
    Guid campMemberId, CancellationToken cancellationToken = default);

Task<int> GetGrantedCountForSeasonAsync(
    Guid campSeasonId, CancellationToken cancellationToken = default);

Task SaveMemberAsync(CampMember member, CancellationToken cancellationToken = default);
```

`SaveMemberAsync` already exists — confirm before adding. If it does, skip that one.

In `CampRepository.cs`:

```csharp
public async Task<CampMember?> GetMemberWithSeasonAsync(
    Guid campMemberId, CancellationToken cancellationToken = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
    return await ctx.CampMembers
        .Include(m => m.CampSeason)
        .FirstOrDefaultAsync(m => m.Id == campMemberId, cancellationToken);
}

public async Task<int> GetGrantedCountForSeasonAsync(
    Guid campSeasonId, CancellationToken cancellationToken = default)
{
    await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
    return await ctx.CampMembers
        .CountAsync(m => m.CampSeasonId == campSeasonId
                      && m.HasEarlyEntry
                      && m.Status == CampMemberStatus.Active,
                    cancellationToken);
}
```

- [ ] **Step 4: Implement service method**

Replace the stub in `CampService.cs`:

```csharp
public async Task<SetEarlyEntryOutcome> SetEarlyEntryAsync(
    Guid campMemberId, bool granted, Guid actorUserId,
    CancellationToken cancellationToken = default)
{
    var member = await _repo.GetMemberWithSeasonAsync(campMemberId, cancellationToken);
    if (member is null) return SetEarlyEntryOutcome.MemberNotFound;

    if (member.HasEarlyEntry == granted)
        return SetEarlyEntryOutcome.NoChange;

    if (granted)
    {
        if (member.Status != CampMemberStatus.Active)
            return SetEarlyEntryOutcome.MemberNotActive;

        var current = await _repo.GetGrantedCountForSeasonAsync(member.CampSeasonId, cancellationToken);
        if (current >= member.CampSeason.EeSlotCount)
            return SetEarlyEntryOutcome.SlotCapExceeded;
    }

    member.HasEarlyEntry = granted;
    await _repo.SaveMemberAsync(member, cancellationToken);

    await _auditLog.LogAsync(
        granted ? AuditAction.CampEarlyEntryGranted : AuditAction.CampEarlyEntryRevoked,
        nameof(CampMember), member.Id,
        granted
            ? $"Granted Early Entry to member in season {member.CampSeason.Year}."
            : $"Revoked Early Entry from member in season {member.CampSeason.Year}.",
        actorUserId,
        relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp),
        cancellationToken: cancellationToken);

    return SetEarlyEntryOutcome.Success;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetEarlyEntryAsync_Grant|FullyQualifiedName~SetEarlyEntryAsync_Revoke"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Services/Camps/CampService.cs \
        src/Humans.Application/Interfaces/Repositories/ICampRepository.cs \
        src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs \
        tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs
git commit -m "issue-490: SetEarlyEntryAsync grant/revoke happy path"
```

### Task 8: `SetEarlyEntryAsync` overflow rejection

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
[HumansFact]
public async Task SetEarlyEntryAsync_Grant_ReturnsSlotCapExceeded_WhenCapWouldBeBreached()
{
    await SeedSettingsAsync();
    var (_, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 2);
    await SeedActiveMemberWithEarlyEntryAsync(season.Id);
    await SeedActiveMemberWithEarlyEntryAsync(season.Id);
    var newMember = await SeedActiveMemberAsync(season.Id);

    var outcome = await _service.SetEarlyEntryAsync(newMember.Id, granted: true, Guid.NewGuid());

    outcome.Should().Be(SetEarlyEntryOutcome.SlotCapExceeded);

    var reloaded = await _dbContext.CampMembers.FirstAsync(m => m.Id == newMember.Id);
    reloaded.HasEarlyEntry.Should().BeFalse();

    await _auditLog.DidNotReceive().LogAsync(
        AuditAction.CampEarlyEntryGranted,
        Arg.Any<string>(), Arg.Any<Guid>(),
        Arg.Any<string>(), Arg.Any<Guid?>(),
        Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetEarlyEntryAsync_Grant_ReturnsSlotCapExceeded"`
Expected: PASS (implementation in Task 7 already covers this path).

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs
git commit -m "issue-490: test SetEarlyEntryAsync rejects overflow"
```

### Task 9: `SetEarlyEntryAsync` rejects non-Active members

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
[HumansFact]
public async Task SetEarlyEntryAsync_Grant_ReturnsMemberNotActive_WhenMemberIsPending()
{
    await SeedSettingsAsync();
    var (_, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
    var member = new CampMember
    {
        Id = Guid.NewGuid(),
        CampSeasonId = season.Id,
        UserId = Guid.NewGuid(),
        Status = CampMemberStatus.Pending,
        RequestedAt = _clock.GetCurrentInstant(),
    };
    _dbContext.CampMembers.Add(member);
    await _dbContext.SaveChangesAsync();

    var outcome = await _service.SetEarlyEntryAsync(member.Id, granted: true, Guid.NewGuid());

    outcome.Should().Be(SetEarlyEntryOutcome.MemberNotActive);

    var reloaded = await _dbContext.CampMembers.FirstAsync(m => m.Id == member.Id);
    reloaded.HasEarlyEntry.Should().BeFalse();
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetEarlyEntryAsync_Grant_ReturnsMemberNotActive"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs
git commit -m "issue-490: test SetEarlyEntryAsync rejects non-Active member"
```

### Task 10: `SetEarlyEntryAsync` idempotency

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
[HumansFact]
public async Task SetEarlyEntryAsync_Idempotent_ReturnsNoChangeAndWritesNoAudit()
{
    await SeedSettingsAsync();
    var (_, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
    var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);

    var outcome = await _service.SetEarlyEntryAsync(member.Id, granted: true, Guid.NewGuid());

    outcome.Should().Be(SetEarlyEntryOutcome.NoChange);

    await _auditLog.DidNotReceive().LogAsync(
        AuditAction.CampEarlyEntryGranted,
        Arg.Any<string>(), Arg.Any<Guid>(),
        Arg.Any<string>(), Arg.Any<Guid?>(),
        Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SetEarlyEntryAsync_Idempotent"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs
git commit -m "issue-490: test SetEarlyEntryAsync idempotency"
```

### Task 11: Consolidate the four "transition to Removed" paths into one helper

**Files:**
- Modify: `src/Humans.Application/Services/Camps/CampService.cs`
- Modify: `tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs`
- Modify: `tests/Humans.Application.Tests/Services/CampServiceTests.cs` (for the Remove-now-cascades-roles test)

**Why this task exists:** `CampService` currently has **four** methods that all transition a `CampMember` to `Removed` (`RejectCampMemberAsync`, `RemoveCampMemberAsync`, `WithdrawCampMembershipRequestAsync`, `LeaveCampAsync`). Each one repeats the same five-step pattern: status guard → optional role cascade → set `Status = Removed` + `RemovedAt = now` + `RemovedByUserId = actor` → `SaveMemberAsync` → audit. Dropping `HasEarlyEntry = false` into each of the four would deepen the duplication, not unwind it. We extract a private helper so the EE clear lives in **one** place, and the four public methods shrink to their actual differences: status precondition, audit action enum value, and post-side-effects (notifications, badge invalidation).

**Bug fix bundled in:** the section invariant doc (`docs/sections/Camps.md` line 210) states that Remove/Leave/Withdraw all cascade role assignments. The code only cascades in Leave (line 1624) and Withdraw (line 1595) — `RemoveCampMemberAsync` does not. Peter authorized fixing this here (2026-05-10) since the helper introduces a `cascadeRoleAssignments` parameter and routing Remove through it aligns code with docs at zero extra cost.

- [ ] **Step 1: Write the failing EE-clear test for each removal path**

In `CampServiceEarlyEntryTests.cs`, append:

```csharp
[HumansFact]
public async Task RemoveCampMemberAsync_ClearsHasEarlyEntry()
{
    await SeedSettingsAsync();
    var (camp, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
    var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);

    await _service.RemoveCampMemberAsync(camp.Id, member.Id, Guid.NewGuid());

    var reloaded = await _dbContext.CampMembers.FirstAsync(m => m.Id == member.Id);
    reloaded.HasEarlyEntry.Should().BeFalse();
    reloaded.Status.Should().Be(CampMemberStatus.Removed);
}

[HumansFact]
public async Task LeaveCampAsync_ClearsHasEarlyEntry()
{
    await SeedSettingsAsync();
    var (_, season) = await SeedCampWithSeasonAsync(initialEeSlotCount: 5);
    var member = await SeedActiveMemberWithEarlyEntryAsync(season.Id);

    await _service.LeaveCampAsync(member.Id, member.UserId);

    var reloaded = await _dbContext.CampMembers.FirstAsync(m => m.Id == member.Id);
    reloaded.HasEarlyEntry.Should().BeFalse();
}
```

- [ ] **Step 2: Write the failing Remove-now-cascades-roles test**

In `CampServiceTests.cs` (the existing file, alongside other removal-path tests), append:

```csharp
[HumansFact]
public async Task RemoveCampMemberAsync_CascadesRoleAssignments()
{
    // Bug fix bundled with issue-490: the section invariants doc says Remove
    // cascades role assignments, but the code historically did not. Route
    // through the new TransitionMemberToRemovedAsync helper closes that gap.
    var memberId = Guid.NewGuid();
    var camp = await SeedCampAsync(); // existing helper in this test class
    var member = await SeedActiveMemberAsync(camp.Seasons.Single().Id, memberId);

    await _service.RemoveCampMemberAsync(camp.Id, memberId, Guid.NewGuid());

    await _campRoleService.Received(1).RemoveAllForMemberAsync(
        memberId, Arg.Any<Guid>(), Arg.Any<CancellationToken>());
}
```

If `SeedCampAsync` / `SeedActiveMemberAsync` don't already exist in `CampServiceTests`, use whichever existing helpers it does have to land a camp + active member; the assertion that matters is `_campRoleService.Received(1).RemoveAllForMemberAsync(...)`.

- [ ] **Step 3: Run new tests to verify they fail**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~RemoveCampMemberAsync_ClearsHasEarlyEntry|FullyQualifiedName~LeaveCampAsync_ClearsHasEarlyEntry|FullyQualifiedName~RemoveCampMemberAsync_CascadesRoleAssignments"`
Expected: FAIL — current code does not touch `HasEarlyEntry`, and Remove does not cascade roles.

- [ ] **Step 4: Extract the helper**

In `CampService.cs`, find a private-methods region (or add one) and add:

```csharp
/// <summary>
/// Single transition point for moving a CampMember to Removed. Handles the
/// role-assignment cascade, the state/timestamp flip (including clearing
/// HasEarlyEntry), and the audit-log entry. Callers handle status preconditions
/// before invoking and any post-effects (notifications, lead-badge
/// invalidation) afterward.
/// </summary>
private async Task TransitionMemberToRemovedAsync(
    CampMember member,
    Guid actorUserId,
    AuditAction auditAction,
    string auditMessage,
    bool cascadeRoleAssignments,
    CancellationToken cancellationToken)
{
    if (cascadeRoleAssignments)
    {
        await _campRoleService.Value.RemoveAllForMemberAsync(
            member.Id, actorUserId, cancellationToken);
    }

    var now = _clock.GetCurrentInstant();
    member.Status = CampMemberStatus.Removed;
    member.RemovedAt = now;
    member.RemovedByUserId = actorUserId;
    member.HasEarlyEntry = false;
    await _repo.SaveMemberAsync(member, cancellationToken);

    await _auditLog.LogAsync(
        auditAction, nameof(CampMember), member.Id,
        auditMessage, actorUserId,
        relatedEntityId: member.CampSeason.CampId, relatedEntityType: nameof(Camp),
        cancellationToken: cancellationToken);
}
```

- [ ] **Step 5: Route `RejectCampMemberAsync` through the helper**

Replace the body of `RejectCampMemberAsync` (after the status guard) so that the manual status/timestamp/save/audit block becomes a single helper call. The rest of the method (loading the camp for the notification, sending the rejection notification, calling `InvalidateLeadBadgesAsync`) stays as-is.

```csharp
public async Task RejectCampMemberAsync(
    Guid scopedCampId, Guid campMemberId, Guid rejectedByUserId,
    CancellationToken cancellationToken = default)
{
    var member = await _repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken)
        ?? throw new InvalidOperationException("Camp member record not found.");

    if (member.Status != CampMemberStatus.Pending)
        throw new InvalidOperationException($"Cannot reject a camp member with status {member.Status}.");

    var requesterUserId = member.UserId;
    var seasonId = member.CampSeasonId;

    await TransitionMemberToRemovedAsync(
        member, rejectedByUserId,
        AuditAction.CampMemberRejected,
        $"Rejected camp membership request for season {member.CampSeason.Year}",
        cascadeRoleAssignments: false,
        cancellationToken);

    await InvalidateLeadBadgesAsync(scopedCampId, cancellationToken);

    var camp = await _repo.GetByIdAsync(scopedCampId, cancellationToken);
    var campName = camp?.Seasons.FirstOrDefault(s => s.Id == seasonId)?.Name ?? camp?.Slug ?? "a camp";
    try
    {
        await _notificationEmitter.SendAsync(
            NotificationSource.CampMembershipRejected,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            $"Your request to join {campName} was not approved",
            [requesterUserId],
            cancellationToken: cancellationToken);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to notify requester {UserId} about rejected camp membership {MemberId}", requesterUserId, member.Id);
    }
}
```

- [ ] **Step 6: Route `RemoveCampMemberAsync` through the helper — cascade ON (bug fix)**

```csharp
public async Task RemoveCampMemberAsync(
    Guid scopedCampId, Guid campMemberId, Guid removedByUserId,
    CancellationToken cancellationToken = default)
{
    var member = await _repo.GetMemberForCampMutationAsync(campMemberId, scopedCampId, cancellationToken)
        ?? throw new InvalidOperationException("Camp member record not found.");

    if (member.Status != CampMemberStatus.Active)
        throw new InvalidOperationException($"Cannot remove a camp member with status {member.Status}.");

    await TransitionMemberToRemovedAsync(
        member, removedByUserId,
        AuditAction.CampMemberRemoved,
        $"Removed camp member from season {member.CampSeason.Year}",
        cascadeRoleAssignments: true,
        cancellationToken);
}
```

- [ ] **Step 7: Route `WithdrawCampMembershipRequestAsync` through the helper**

```csharp
public async Task WithdrawCampMembershipRequestAsync(
    Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
{
    var member = await _repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken)
        ?? throw new InvalidOperationException("Camp member record not found.");

    if (member.Status != CampMemberStatus.Pending)
        throw new InvalidOperationException($"Cannot withdraw a camp member request with status {member.Status}.");

    await TransitionMemberToRemovedAsync(
        member, userId,
        AuditAction.CampMemberWithdrawn,
        $"Withdrew camp membership request for season {member.CampSeason.Year}",
        cascadeRoleAssignments: true,
        cancellationToken);

    await InvalidateLeadBadgesAsync(member.CampSeason.CampId, cancellationToken);
}
```

- [ ] **Step 8: Route `LeaveCampAsync` through the helper**

```csharp
public async Task LeaveCampAsync(
    Guid campMemberId, Guid userId, CancellationToken cancellationToken = default)
{
    var member = await _repo.GetMemberForOwnMutationAsync(campMemberId, userId, cancellationToken)
        ?? throw new InvalidOperationException("Camp member record not found.");

    if (member.Status != CampMemberStatus.Active)
        throw new InvalidOperationException($"Cannot leave a camp membership with status {member.Status}.");

    await TransitionMemberToRemovedAsync(
        member, userId,
        AuditAction.CampMemberLeft,
        $"Left camp season {member.CampSeason.Year}",
        cascadeRoleAssignments: true,
        cancellationToken);
}
```

- [ ] **Step 9: Run new tests to verify they pass**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~RemoveCampMemberAsync_ClearsHasEarlyEntry|FullyQualifiedName~LeaveCampAsync_ClearsHasEarlyEntry|FullyQualifiedName~RemoveCampMemberAsync_CascadesRoleAssignments"`
Expected: PASS.

- [ ] **Step 10: Run the full Camp test suite to confirm no regression**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CampServiceTests|FullyQualifiedName~CampServiceEarlyEntryTests"`
Expected: PASS — the existing approve/reject/remove/withdraw/leave tests should still pass because the observable behavior (status, RemovedAt, RemovedByUserId, audit action, notification) is unchanged. The only behavior change is Remove now cascades roles, covered by Step 2's test.

- [ ] **Step 11: Commit**

```bash
git add src/Humans.Application/Services/Camps/CampService.cs \
        tests/Humans.Application.Tests/Services/CampServiceEarlyEntryTests.cs \
        tests/Humans.Application.Tests/Services/CampServiceTests.cs
git commit -m "issue-490: consolidate Removed-transition into helper; fix Remove cascade

Four CampService methods (Reject/Remove/Withdraw/Leave) all transitioned
members to Removed via the same five-step block. Extract a private
TransitionMemberToRemovedAsync that handles cascade + state flip + save
+ audit. HasEarlyEntry now clears in one place; Remove now cascades role
assignments per the section invariant doc (Peter-authorized bug fix)."
```

---

## Phase 4 — Web layer (controllers + views)

### Task 12: CampController endpoints for grant/revoke

**Files:**
- Modify: `src/Humans.Web/Controllers/CampController.cs`

The existing `Members/Approve`, `Members/Reject`, `Members/Remove` endpoints use resource-based authz via `IAuthorizationService.AuthorizeAsync(User, camp, CampOperationRequirement.Manage)`. EE grant/revoke uses the same authz (Primary, CoLead, CampAdmin, Admin all already satisfy `Manage`).

- [ ] **Step 1: Locate the existing `Members/Approve` action**

Run: `grep -n "Members/Approve\|Members/Remove\|CampOperationRequirement.Manage" src/Humans.Web/Controllers/CampController.cs`

Read the surrounding 30 lines so you can mirror the exact pattern (route shape, AuthorizeAsync invocation, success/error redirects).

- [ ] **Step 2: Add the EarlyEntry endpoint**

Add this action alongside the other member-mutation actions (search for `Members/Remove` and place this one next to it):

```csharp
[HttpPost("Members/{campMemberId:guid}/EarlyEntry")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SetMemberEarlyEntry(
    string slug, Guid campMemberId, bool granted, CancellationToken cancellationToken)
{
    var camp = await _campService.GetCampBySlugAsync(slug, cancellationToken);
    if (camp is null) return NotFound();

    var authz = await _authorizationService.AuthorizeAsync(
        User, camp, CampOperationRequirement.Manage);
    if (!authz.Succeeded) return Forbid();

    var actorIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
    if (actorIdClaim is null || !Guid.TryParse(actorIdClaim.Value, out var actorId))
        return Forbid();

    var outcome = await _campService.SetEarlyEntryAsync(
        campMemberId, granted, actorId, cancellationToken);

    switch (outcome)
    {
        case SetEarlyEntryOutcome.Success:
            SetSuccess(granted ? "Early Entry granted." : "Early Entry revoked.");
            break;
        case SetEarlyEntryOutcome.NoChange:
            // Silent — UI already reflected the state.
            break;
        case SetEarlyEntryOutcome.SlotCapExceeded:
            SetError("Cannot grant Early Entry: slot cap reached for this camp.");
            break;
        case SetEarlyEntryOutcome.MemberNotActive:
            SetError("Only Active camp members can hold Early Entry.");
            break;
        case SetEarlyEntryOutcome.MemberNotFound:
            return NotFound();
    }

    return RedirectToAction(nameof(Members), new { slug });
}
```

Add the matching using if not already present:

```csharp
using Humans.Application.Services.Camps; // for SetEarlyEntryOutcome
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/CampController.cs
git commit -m "issue-490: CampController.SetMemberEarlyEntry endpoint"
```

### Task 13: CampAdminController endpoints for slot count + EE date

**Files:**
- Modify: `src/Humans.Web/Controllers/CampAdminController.cs`

- [ ] **Step 1: Add the slot-count endpoint**

After the `SetNameLockDate` action (~line 261), add:

```csharp
[HttpPost("SetCampSeasonEeSlotCount/{seasonId:guid}")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SetCampSeasonEeSlotCount(
    Guid seasonId, int slotCount, CancellationToken cancellationToken)
{
    var actorIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
    if (actorIdClaim is null || !Guid.TryParse(actorIdClaim.Value, out var actorId))
        return Forbid();

    try
    {
        await _campService.SetCampSeasonEeSlotCountAsync(
            seasonId, slotCount, actorId, cancellationToken);
        SetSuccess($"EE slot count set to {slotCount}.");
    }
    catch (ArgumentOutOfRangeException)
    {
        SetError("EE slot count cannot be negative.");
    }
    catch (InvalidOperationException ex)
    {
        _logger.LogWarning(ex, "Failed to set EE slot count on season {SeasonId}", seasonId);
        SetError(ex.Message);
    }

    return RedirectToAction(nameof(Index));
}
```

- [ ] **Step 2: Add the EE start date endpoint**

After the new slot-count action, add:

```csharp
[HttpPost("SetEeStartDate")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SetEeStartDate(string? eeStartDate, CancellationToken cancellationToken)
{
    LocalDate? parsed = null;
    if (!string.IsNullOrWhiteSpace(eeStartDate))
    {
        var parseResult = NodaTime.Text.LocalDatePattern.Iso.Parse(eeStartDate);
        if (!parseResult.Success)
        {
            SetError("Invalid date format. Use yyyy-MM-dd.");
            return RedirectToAction(nameof(Index));
        }
        parsed = parseResult.Value;
    }

    try
    {
        await _campService.SetEeStartDateAsync(parsed, cancellationToken);
        SetSuccess(parsed.HasValue
            ? $"EE start date set to {parsed.Value}."
            : "EE start date cleared.");
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to set EE start date");
        SetError($"Failed to set EE start date: {ex.Message}");
    }

    return RedirectToAction(nameof(Index));
}
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/CampAdminController.cs
git commit -m "issue-490: CampAdminController EE slot count + start date endpoints"
```

### Task 14: View — `/Camps/Admin` EE column + totals + start-date editor

**Files:**
- Modify: `src/Humans.Web/Views/Camp/Admin.cshtml` (path may differ — check `src/Humans.Web/Views/CampAdmin/Index.cshtml` too)
- Modify: a view-model class if `Admin` uses one (typically `CampAdminIndexViewModel` or similar in `src/Humans.Web/ViewModels/`)

- [ ] **Step 1: Locate the admin index view and its view-model**

Run: `grep -rn "CampAdminController\|public class CampAdminController" src/Humans.Web | head -5`
Run: `find src/Humans.Web/Views -iname "*.cshtml" | xargs grep -l "PublicYear\|SetPublicYear\|SetNameLockDate" 2>/dev/null`

Open the view file the second grep finds.

- [ ] **Step 2: Plumb EE state through the view-model**

The Admin index needs, per camp/season row: `EeSlotCount` and `EeGrantedCount` (count of Active members where HasEarlyEntry=true). It also needs `Settings.EeStartDate`.

Find the action that returns this view (`CampAdminController.Index`). It probably loads `Camps` (each with current season) and `Settings`. Add EeGrantedCount to whatever projection it builds — call `_dbContext.CampMembers.GroupBy(...)` or, more correctly, add a repository helper:

In `ICampRepository.cs`:

```csharp
Task<IReadOnlyDictionary<Guid, int>> GetEarlyEntryGrantedCountsBySeasonAsync(
    IReadOnlyCollection<Guid> seasonIds, CancellationToken cancellationToken = default);
```

In `CampRepository.cs`:

```csharp
public async Task<IReadOnlyDictionary<Guid, int>> GetEarlyEntryGrantedCountsBySeasonAsync(
    IReadOnlyCollection<Guid> seasonIds, CancellationToken cancellationToken = default)
{
    if (seasonIds.Count == 0)
        return new Dictionary<Guid, int>();
    await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
    return await ctx.CampMembers
        .Where(m => seasonIds.Contains(m.CampSeasonId)
                 && m.HasEarlyEntry
                 && m.Status == CampMemberStatus.Active)
        .GroupBy(m => m.CampSeasonId)
        .Select(g => new { Id = g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
}
```

Expose it via `ICampService` only if the budget allows. Otherwise, the `CampAdminController` can inject `ICampRepository` directly — but that violates `memory/architecture/repository-required-for-db-access.md` (controllers should not inject repositories). Instead, add it to `ICampService` only if no method can be removed. *Decision: add to `ICampService` as `GetEarlyEntryGrantedCountsAsync(IReadOnlyCollection<Guid> seasonIds, CancellationToken)` — but that pushes the budget to 55. **STOP and ask Peter before adding** unless he has already authorized 55 in conversation. If not authorized, pass the data through an existing read method by extending its return shape (e.g., the projection used by the admin index).*

Pragmatic alternative (no service surface growth): add a property to the existing view-model row populated inside `CampAdminController.Index` by calling `_campRepository` through a method that already exists on `ICampService` returning the seasons, then projecting in the controller. **Use only if such a method already exists; otherwise ask Peter.**

- [ ] **Step 3: Add EE Slots column and totals strip in the view**

Inside the admin index view, near the existing camp/season table:

1. Above the table, add a totals strip and EE start date editor:

```cshtml
<div class="ee-summary alert alert-info">
    <strong>Early Entry (this year):</strong>
    @{
        var totalSlots = Model.Camps.Sum(c => c.CurrentSeason?.EeSlotCount ?? 0);
        var totalGranted = Model.Camps.Sum(c => c.CurrentSeason is null ? 0 : Model.EeGrantedCounts.GetValueOrDefault(c.CurrentSeason.Id));
    }
    <span>@totalGranted granted across @totalSlots configured slots</span>
    @if (totalGranted > totalSlots)
    {
        <span class="badge bg-warning ms-2">over cap</span>
    }
</div>

<form method="post" asp-action="SetEeStartDate" class="d-inline-block ms-3">
    @Html.AntiForgeryToken()
    <label class="form-label">EE start date:</label>
    <input type="date" name="eeStartDate"
           value="@(Model.Settings.EeStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))" />
    <button type="submit" class="btn btn-sm btn-primary">Save</button>
</form>
```

2. In the table header, add a column header `EE Slots`.

3. In each table row, add (mirroring the existing inline-edit style for `SetNameLockDate`):

```cshtml
<td>
    <form method="post" asp-action="SetCampSeasonEeSlotCount"
          asp-route-seasonId="@camp.CurrentSeason?.Id" class="d-inline">
        @Html.AntiForgeryToken()
        <input type="number" name="slotCount" min="0"
               value="@(camp.CurrentSeason?.EeSlotCount ?? 0)"
               class="form-control form-control-sm d-inline" style="width:5rem" />
        @{
            var granted = camp.CurrentSeason is null ? 0 : Model.EeGrantedCounts.GetValueOrDefault(camp.CurrentSeason.Id);
            var cap = camp.CurrentSeason?.EeSlotCount ?? 0;
        }
        <small class="text-muted ms-1">@granted / @cap</small>
        @if (granted > cap)
        {
            <span class="badge bg-warning ms-1">over</span>
        }
        <button type="submit" class="btn btn-sm btn-outline-primary ms-1">Save</button>
    </form>
</td>
```

- [ ] **Step 4: Build + run the app to eyeball**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds.

Optionally run the dev server (`dotnet run --project src/Humans.Web`) and visit `/Camps/Admin` as CampAdmin to confirm the column renders and the form posts.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/Camp/Admin.cshtml \
        src/Humans.Web/ViewModels/<the-view-model>.cs \
        src/Humans.Web/Controllers/CampAdminController.cs \
        src/Humans.Application/Interfaces/Repositories/ICampRepository.cs \
        src/Humans.Infrastructure/Repositories/Camps/CampRepository.cs
git commit -m "issue-490: /Camps/Admin EE slots column + start date editor"
```

### Task 15: View — `/Camps/{slug}/Edit/Members` EE toggle column

**Files:**
- Modify: `src/Humans.Web/Views/Camp/Members.cshtml`
- Modify: the view-model class for `Members` (likely `CampMembersViewModel` or similar)
- Modify: `CampController.Members` action to project `HasEarlyEntry` and `EeSlotCount` + computed `EeGrantedCount` for the season

- [ ] **Step 1: Read the existing `Members.cshtml` and identify the active-members table**

Run: `grep -n "CampMemberStatus.Active\|HasEarlyEntry\|asp-action.*Members" src/Humans.Web/Views/Camp/Members.cshtml`

The active-members table is where the EE toggle column goes.

- [ ] **Step 2: Plumb EE state through `CampController.Members`**

Find the action body (in `CampController.cs`, the `Members` action). It loads `camp` + members + roles. Extend the view-model to include:

- per-row `HasEarlyEntry` (bool)
- season `EeSlotCount` (int)
- season `EeGrantedCount` (int)

Use the data already in `_campService.GetSeasonMembersAsync` or wherever it pulls members from — `HasEarlyEntry` rides along on each `CampMember`. For the count, count locally in the controller from the already-loaded member list.

- [ ] **Step 3: Render the EE toggle column**

In the active-members table, add a header `EE` and a per-row cell:

```cshtml
<th>EE</th>
```

```cshtml
<td>
    @{
        var canGrant = Model.EeGrantedCount < Model.EeSlotCount;
        var disabled = !memberRow.HasEarlyEntry && !canGrant;
    }
    <form method="post" asp-action="SetMemberEarlyEntry"
          asp-route-slug="@Model.Slug" asp-route-campMemberId="@memberRow.Id"
          class="d-inline">
        @Html.AntiForgeryToken()
        <input type="hidden" name="granted" value="@((!memberRow.HasEarlyEntry).ToString().ToLowerInvariant())" />
        <button type="submit"
                class="btn btn-sm @(memberRow.HasEarlyEntry ? "btn-success" : "btn-outline-secondary")"
                @(disabled ? "disabled" : "")>
            @(memberRow.HasEarlyEntry ? "✓ EE" : "Grant EE")
        </button>
    </form>
</td>
```

- [ ] **Step 4: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/Camp/Members.cshtml \
        src/Humans.Web/ViewModels/<the-members-vm>.cs \
        src/Humans.Web/Controllers/CampController.cs
git commit -m "issue-490: Camp members page EE toggle column"
```

### Task 16: Public detail page asserts no EE rendering

**Files:**
- Modify or Create: a controller/view test under `tests/Humans.Web.Tests/` or similar (match the existing test pattern — search for any existing `CampControllerTests` or `CampDetailsTests`)

- [ ] **Step 1: Find existing public-detail-page test pattern**

Run: `find tests -iname "*Camp*Test*.cs" | head -10`

If no controller-level test exists for the public detail page, an alternative is a service-layer test asserting that the data shape returned for the public detail page does not contain `HasEarlyEntry` or `EeSlotCount`. Choose whichever matches existing patterns.

- [ ] **Step 2: Write the assertion test**

If a view-rendering test exists (e.g., using `WebApplicationFactory<Program>`), add:

```csharp
[HumansFact]
public async Task PublicCampDetail_DoesNotRender_EarlyEntryState()
{
    var response = await _client.GetAsync($"/Camps/{_camp.Slug}");
    response.EnsureSuccessStatusCode();
    var html = await response.Content.ReadAsStringAsync();

    html.Should().NotContain("Early Entry");
    html.Should().NotContain("HasEarlyEntry");
    html.Should().NotContain("EeSlot");
}
```

If only service-layer tests exist, assert the shape of `BuildCampDetailDataAsync`'s return (it should not project `HasEarlyEntry` or `EeSlotCount`).

- [ ] **Step 3: Run the test**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~PublicCampDetail_DoesNotRender_EarlyEntryState"`
Expected: PASS (the existing Details view doesn't render EE; this test pins that down).

- [ ] **Step 4: Commit**

```bash
git add tests/
git commit -m "issue-490: pin invariant — public camp detail never renders EE"
```

---

## Phase 5 — Docs + Final Sweep

### Task 17: Update `docs/sections/Camps.md`

**Files:**
- Modify: `docs/sections/Camps.md`

- [ ] **Step 1: Add EE to the data-model section**

In the `### CampMember` table (around line 87), add a row:

```markdown
| HasEarlyEntry | bool | Default false; cleared on Removed transition |
```

In the `### CampSeason` block, mention `EeSlotCount`. If CampSeason has a property table, add the row; if it's prose, add a sentence.

In the `### CampSettings` block, add a sentence noting `EeStartDate`.

- [ ] **Step 2: Add EE invariants**

Append to the **Invariants** section:

```markdown
- Early Entry slot count is per-season (`CampSeason.EeSlotCount`, CampAdmin-managed). The EE start date is global per year (`CampSettings.EeStartDate`).
- A `CampMember.HasEarlyEntry` grant requires `Status = Active`. Granting beyond `EeSlotCount` is rejected; lowering `EeSlotCount` below current grants is allowed (no auto-revoke; overflow flagged in UI).
- Member-removal transitions (Remove / Leave / Withdraw / Reject) clear `HasEarlyEntry` in the same `SaveChangesAsync` as the status flip.
- EE state is **never** rendered on anonymous or public views — only on `/Camps/Admin` and `/Camps/{slug}/Edit/Members` for CampAdmin/leads.
```

Append to **Negative Access Rules**:

```markdown
- Camp leads **cannot** edit `EeSlotCount` (CampAdmin/Admin only).
- Anyone **cannot** grant EE to a non-Active member (service rejects with `MemberNotActive`).
```

Append to **Triggers**:

```markdown
- Granting / revoking EE writes `CampEarlyEntryGranted` / `CampEarlyEntryRevoked` audit entries. Idempotent set writes no audit row.
- Changing `EeSlotCount` writes `CampSeasonEeSlotCountChanged`; changing `EeStartDate` writes `CampSettingsEeStartDateChanged`.
```

- [ ] **Step 3: Add the new routes to the Routing table**

Append rows:

```markdown
| `/Camps/{slug}/Members/{campMemberId}/EarlyEntry` | `CampController` | Grant / revoke EE on a camp member |
| `/Camps/Admin/SetCampSeasonEeSlotCount/{seasonId}` | `CampAdminController` | Set a season's EE slot cap |
| `/Camps/Admin/SetEeStartDate` | `CampAdminController` | Set the global EE start date |
```

- [ ] **Step 4: Commit**

```bash
git add docs/sections/Camps.md
git commit -m "issue-490: update Camps section doc with EE invariants"
```

### Task 18: Full build + test sweep + push

- [ ] **Step 1: Full build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Full test sweep**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all pass.

- [ ] **Step 3: Architecture test**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Architecture"`
Expected: budget test passes at 54, all other arch tests pass.

- [ ] **Step 4: Push branch**

```bash
git push origin issue-490-early-entry
```

- [ ] **Step 5: Open PR**

```bash
gh pr create --title "issue-490: Early Entry — camps consumer (v1)" --body "$(cat <<'EOF'
## Summary
- Adds `CampSeason.EeSlotCount`, `CampMember.HasEarlyEntry`, `CampSettings.EeStartDate`.
- 3 new `ICampService` methods (budget 51→54, Peter-authorized 2026-05-10; logged in ratchet).
- `/Camps/Admin` gains EE slots column + EE start date editor.
- `/Camps/{slug}/Edit/Members` gains per-member EE toggle.
- Public detail page does not render EE (pinned by test).
- Member-removal paths clear `HasEarlyEntry`.

Closes nobodies-collective/Humans#490

## Test plan
- [ ] Service tests cover grant/revoke, overflow, non-Active rejection, idempotency, cascade-on-removal.
- [ ] Architecture budget test passes at 54.
- [ ] Manual: CampAdmin sets slot count and start date.
- [ ] Manual: Camp lead grants EE; overflow blocked.
- [ ] Manual: Public `/Camps/{slug}` renders no EE.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL printed to stdout.

---

## Self-review notes (filled by author at write time)

**Spec coverage:**
- Entity changes ✓ (Task 1)
- Migration ✓ (Task 2)
- Audit values ✓ (Task 3)
- Service surface ✓ (Task 4)
- Service rules — overflow ✓ (Task 8), non-Active ✓ (Task 9), idempotency ✓ (Task 10), reduce-below allowed ✓ (Task 6), member-removal cascade ✓ (Task 11, via the new `TransitionMemberToRemovedAsync` helper that absorbs the four-way duplication and fixes the Remove-doesn't-cascade-roles bug as a side benefit)
- Authz ✓ (Tasks 12, 13 — resource-based `Manage` for lead set, `[Authorize(Policy=CampAdminOrAdmin)]` for admin set, already on the controller)
- UI Admin ✓ (Task 14)
- UI Members ✓ (Task 15)
- Public no-render invariant ✓ (Task 16)
- Audit on grant/revoke/slot-change/date-change ✓ (Tasks 5, 6, 7)
- Section doc ✓ (Task 17)

**Open risks flagged in-line:**
- Task 5 step 4: `IAuditLogService.LogAsync` signature for the `actorUserId: null` case may need verification.
- Task 14 step 2: surfacing `EeGrantedCounts` to the Admin view-model may require a 4th method on `ICampService`, which would breach the budget again. Flagged with a "STOP and ask Peter" note.
