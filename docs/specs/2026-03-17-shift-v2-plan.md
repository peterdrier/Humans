# Shift Management v2 Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add build/strike all-day shifts with date-range signup, remove shift titles, replace deactivation with deletion, add event rota bulk generation, general volunteer pool, and role period tags.

**Architecture:** Extends existing Clean Architecture layers. New enums and entity changes in Domain, new/updated EF configurations in Infrastructure, new service methods in Application/Infrastructure, updated controllers and views in Web. Single EF migration covers all schema changes.

**Tech Stack:** ASP.NET Core 10, EF Core + PostgreSQL, NodaTime, Hangfire, xUnit

**Spec:** `docs/specs/2026-03-17-shift-v2-design.md`

---

**Important: Chunk 1 build order.** Tasks 2–7 modify entities and configurations, which breaks the build until Tasks 8–15 fix all referencing code. The build will be red throughout Chunk 1 until Task 15 completes. Do NOT expect intermediate builds to pass between Tasks 2 and 15 — commit entity/config changes as you go, but only verify the full build after Task 15. The migration (Task 16) should only be generated after the build is green.

**Note: iCal feed.** The iCal feed controller (`/ICal/{token}.ics`) does not exist yet in the codebase — only the token field on User and the URL display on `/Shifts/Mine`. The design spec references updating the iCal event title format, but this applies when the iCal controller is implemented (Slice 4 from v1). No iCal changes are needed in this plan.

## Chunk 1: Schema Foundation (Slice A)

### Task 1: Create new enums

**Files:**
- Create: `src/Humans.Domain/Enums/RotaPeriod.cs`
- Create: `src/Humans.Domain/Enums/RolePeriod.cs`

- [ ] **Step 1: Create RotaPeriod enum**

```csharp
namespace Humans.Domain.Enums;

public enum RotaPeriod
{
    Build = 0,
    Event = 1,
    Strike = 2
}
```

- [ ] **Step 2: Create RolePeriod enum**

```csharp
namespace Humans.Domain.Enums;

public enum RolePeriod
{
    YearRound = 0,
    Build = 1,
    Event = 2,
    Strike = 3
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Enums/RotaPeriod.cs src/Humans.Domain/Enums/RolePeriod.cs
git commit -m "feat: add RotaPeriod and RolePeriod enums"
```

---

### Task 2: Update Shift entity — remove Title and IsActive, add IsAllDay

**Files:**
- Modify: `src/Humans.Domain/Entities/Shift.cs:25` (Title), `:65` (IsActive)
- Test: `tests/Humans.Domain.Tests/Entities/ShiftTests.cs`

- [ ] **Step 1: Write failing test for IsAllDay**

In `tests/Humans.Domain.Tests/Entities/ShiftTests.cs`, add:

```csharp
[Fact]
public void IsAllDay_DefaultsFalse()
{
    var shift = new Shift();
    Assert.False(shift.IsAllDay);
}

[Fact]
public void IsAllDay_WhenTrue_DurationIgnoredForDisplay()
{
    var shift = new Shift
    {
        IsAllDay = true,
        StartTime = new LocalTime(0, 0),
        Duration = Duration.FromHours(24)
    };
    Assert.True(shift.IsAllDay);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Humans.slnx --filter "IsAllDay"`
Expected: FAIL — `IsAllDay` property does not exist

- [ ] **Step 3: Update Shift entity**

In `src/Humans.Domain/Entities/Shift.cs`:
- Remove the `Title` property (line 25)
- Remove the `IsActive` property (line 65)
- Add `IsAllDay` property:

Also in `src/Humans.Domain/Entities/ShiftSignup.cs`, update the `Cancel()` method's XML doc comment from "shift deactivated, account deletion" to "shift/rota deleted, account deletion".

```csharp
public bool IsAllDay { get; set; }
```

- [ ] **Step 4: Fix existing tests that reference Title or IsActive**

In `tests/Humans.Domain.Tests/Entities/ShiftTests.cs`:
- Remove any test that sets `shift.Title = ...`
- Update any shift construction that sets `Title` — remove the property assignment
- Remove any test that checks `IsActive`

- [ ] **Step 5: Run all shift tests**

Run: `dotnet test Humans.slnx --filter "ShiftTests"`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Domain/Entities/Shift.cs tests/Humans.Domain.Tests/Entities/ShiftTests.cs
git commit -m "feat: remove Title and IsActive from Shift, add IsAllDay"
```

---

### Task 3: Update Rota entity — remove IsActive, add Period and PracticalInfo

**Files:**
- Modify: `src/Humans.Domain/Entities/Rota.cs:50` (IsActive)

- [ ] **Step 1: Update Rota entity**

In `src/Humans.Domain/Entities/Rota.cs`:
- Remove the `IsActive` property (line 50)
- Add:

```csharp
public RotaPeriod Period { get; set; } = RotaPeriod.Event;

[MaxLength(2000)]
public string? PracticalInfo { get; set; }
```

- Add `using Humans.Domain.Enums;` if not already present.

- [ ] **Step 2: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Build errors — code references `Rota.IsActive` in services and views. These will be fixed in subsequent tasks.

- [ ] **Step 3: Commit (entity only)**

```bash
git add src/Humans.Domain/Entities/Rota.cs
git commit -m "feat: remove IsActive from Rota, add Period and PracticalInfo"
```

---

### Task 4: Update ShiftSignup entity — add SignupBlockId

**Files:**
- Modify: `src/Humans.Domain/Entities/ShiftSignup.cs`

- [ ] **Step 1: Add SignupBlockId property**

In `src/Humans.Domain/Entities/ShiftSignup.cs`, add after the existing properties:

```csharp
/// <summary>
/// Shared Guid linking all signups created by a single range signup action.
/// Null for individual event-time signups. Used by BailRangeAsync.
/// </summary>
public Guid? SignupBlockId { get; set; }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Entities/ShiftSignup.cs
git commit -m "feat: add SignupBlockId to ShiftSignup for range bail grouping"
```

---

### Task 5: Create GeneralAvailability entity

**Files:**
- Create: `src/Humans.Domain/Entities/GeneralAvailability.cs`

- [ ] **Step 1: Create entity**

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class GeneralAvailability
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid EventSettingsId { get; set; }
    public List<int> AvailableDayOffsets { get; set; } = [];
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public EventSettings EventSettings { get; set; } = null!;
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Entities/GeneralAvailability.cs
git commit -m "feat: add GeneralAvailability entity for volunteer pool"
```

---

### Task 6: Update TeamRoleDefinition — add Period

**Files:**
- Modify: `src/Humans.Domain/Entities/TeamRoleDefinition.cs`

- [ ] **Step 1: Add Period property**

In `src/Humans.Domain/Entities/TeamRoleDefinition.cs`, add:

```csharp
public RolePeriod Period { get; set; } = RolePeriod.YearRound;
```

Add `using Humans.Domain.Enums;` if not already present.

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Entities/TeamRoleDefinition.cs
git commit -m "feat: add Period to TeamRoleDefinition"
```

---

### Task 7: Update EF configurations

**Files:**
- Modify: `src/Humans.Infrastructure/Data/Configurations/ShiftConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/RotaConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/ShiftSignupConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/TeamRoleDefinitionConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/GeneralAvailabilityConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs` (add DbSet)

- [ ] **Step 1: Update ShiftConfiguration**

In `src/Humans.Infrastructure/Data/Configurations/ShiftConfiguration.cs`:
- Remove `builder.Property(s => s.Title).HasMaxLength(256).IsRequired();` (line 15 — this IS explicitly configured, not convention)
- Note: `IsActive` was configured by convention, not explicitly — removing the entity property is sufficient
- Add: `builder.Property(e => e.IsAllDay).HasDefaultValue(false);`

- [ ] **Step 2: Update RotaConfiguration**

In `src/Humans.Infrastructure/Data/Configurations/RotaConfiguration.cs`:
- Note: `IsActive` was configured by convention — removing the entity property is sufficient
- Add:

```csharp
builder.Property(e => e.Period)
    .HasConversion<string>()
    .HasMaxLength(50)
    .HasDefaultValue(RotaPeriod.Event);

builder.Property(e => e.PracticalInfo)
    .HasMaxLength(2000);
```

- [ ] **Step 3: Update TeamRoleDefinitionConfiguration**

In `src/Humans.Infrastructure/Data/Configurations/TeamRoleDefinitionConfiguration.cs`, add:

```csharp
builder.Property(d => d.Period)
    .HasConversion<string>()
    .HasMaxLength(50)
    .HasDefaultValue(RolePeriod.YearRound);
```

- [ ] **Step 4: Update ShiftSignupConfiguration**

In `src/Humans.Infrastructure/Data/Configurations/ShiftSignupConfiguration.cs`:
- Add: `builder.Property(e => e.SignupBlockId);`
- Add index: `builder.HasIndex(e => e.SignupBlockId);`

- [ ] **Step 5: Create GeneralAvailabilityConfiguration**

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class GeneralAvailabilityConfiguration : IEntityTypeConfiguration<GeneralAvailability>
{
    public void Configure(EntityTypeBuilder<GeneralAvailability> builder)
    {
        builder.ToTable("general_availability");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.AvailableDayOffsets)
            .HasColumnType("jsonb")
            .Metadata.SetValueComparer(new ValueComparer<List<int>>(
                (a, b) => a != null && b != null && a.SequenceEqual(b),
                c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                c => c.ToList()));

        builder.HasIndex(e => new { e.UserId, e.EventSettingsId }).IsUnique();

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.EventSettings)
            .WithMany()
            .HasForeignKey(e => e.EventSettingsId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

- [ ] **Step 6: Add DbSet to HumansDbContext**

In `src/Humans.Infrastructure/Data/HumansDbContext.cs`, add:

```csharp
public DbSet<GeneralAvailability> GeneralAvailability => Set<GeneralAvailability>();
```

- [ ] **Step 7: Build to verify**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj`
Expected: Build errors — services still reference Title/IsActive. That's OK for now.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Infrastructure/Data/Configurations/ src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "feat: update EF configurations for shift v2 schema"
```

---

### Task 8: Update view models

**Files:**
- Modify: `src/Humans.Web/Models/ShiftViewModels.cs:56,66,86,264`

- [ ] **Step 1: Update view models**

In `src/Humans.Web/Models/ShiftViewModels.cs`:
- `CreateRotaModel`: Add `RotaPeriod Period` and `string? PracticalInfo` properties
- `EditRotaModel`: Remove `IsActive` property (line 56). Add `string? PracticalInfo`.
- `CreateShiftModel`: Remove `Title` property (line 66)
- `EditShiftModel`: Remove `IsActive` property (line 86)
- `NoShowHistoryItem`: Rename `ShiftTitle` to `ShiftLabel` (line 264) — will be populated from `shift.Rota.Name + time`
- Add `RotaPeriod Period` to `EditRotaModel`

- [ ] **Step 2: Build to check remaining errors**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build errors in controllers/views referencing removed properties. Fix in next tasks.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/ShiftViewModels.cs
git commit -m "feat: update shift view models for v2 schema"
```

---

### Task 9: Update ShiftManagementService — remove IsActive filters and deactivation

**Files:**
- Modify: `src/Humans.Infrastructure/Services/ShiftManagementService.cs`
- Modify: `src/Humans.Application/Interfaces/IShiftManagementService.cs`

- [ ] **Step 1: Remove deactivation methods from interface**

In `src/Humans.Application/Interfaces/IShiftManagementService.cs`:
- Remove `DeactivateRotaAsync` method signature
- Remove `DeactivateShiftAsync` method signature

- [ ] **Step 2: Remove deactivation methods from service**

In `src/Humans.Infrastructure/Services/ShiftManagementService.cs`:
- Delete `DeactivateRotaAsync` method (lines 214-221)
- Delete `DeactivateShiftAsync` method (lines 306-313)

- [ ] **Step 3: Remove IsActive filters from all queries**

In `src/Humans.Infrastructure/Services/ShiftManagementService.cs`, find and remove all `.IsActive` filter conditions:
- `GetUrgentShiftsAsync` (line 382): remove `s.IsActive && s.Rota.IsActive` from WHERE
- `GetBrowseShiftsAsync` (line 427): remove `s.IsActive && s.Rota.IsActive` from WHERE
- `GetStaffingDataAsync` (line 491): remove `s.IsActive && s.Rota.IsActive` from WHERE
- `GetShiftsSummaryAsync` (line 536): remove `.Where(s => s.IsActive)` filter
- `GetDepartmentsWithRotasAsync` (line 557): remove `&& r.IsActive` from WHERE

- [ ] **Step 4: Update DeleteRotaAsync / DeleteShiftAsync**

Ensure `DeleteRotaAsync` and `DeleteShiftAsync` have confirmed-signup guards:
- If any child shift (for rota) or the shift itself has Confirmed signups, throw with message
- If only Pending signups exist, cancel them before deletion

Update the error messages to use "humans" terminology per CLAUDE.md and include the count:
```csharp
var confirmedCount = rota.Shifts.SelectMany(s => s.ShiftSignups)
    .Count(d => d.Status == SignupStatus.Confirmed);
if (confirmedCount > 0)
    throw new InvalidOperationException(
        $"Cannot delete — {confirmedCount} humans have confirmed signups. Bail or reassign them first.");
```

Apply the same pattern to `DeleteShiftAsync`.

- [ ] **Step 5: Build to check progress**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj`
Expected: Fewer errors — services should compile now

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/IShiftManagementService.cs src/Humans.Infrastructure/Services/ShiftManagementService.cs
git commit -m "refactor: remove deactivation and IsActive filters from shift services"
```

---

### Task 10: Update ShiftSignupService — remove Title and IsActive references

**Files:**
- Modify: `src/Humans.Infrastructure/Services/ShiftSignupService.cs`

- [ ] **Step 1: Remove IsActive guard checks**

In `src/Humans.Infrastructure/Services/ShiftSignupService.cs`, remove the IsActive validation in `SignUpAsync()` (lines 52-53):
```csharp
// DELETE these two lines:
if (!shift.IsActive) return SignupResult.Fail("Shift is not active.");
if (!shift.Rota.IsActive) return SignupResult.Fail("Rota is not active.");
```

Since deactivation is removed, all shifts in the DB are active by definition.

- [ ] **Step 2: Replace ALL Title references with Rota.Name**

In `src/Humans.Infrastructure/Services/ShiftSignupService.cs`, replace ALL occurrences of `shift.Title` and `signup.Shift.Title` with `shift.Rota.Name` / `signup.Shift.Rota.Name`. There are approximately 7 occurrences throughout the file (lines 116, 166, 183, 213, 263, 288, 370). Do a full search for `.Title` in the file and replace every one.

Ensure `shift.Rota` is eagerly loaded in all queries that access it (check for `.Include(s => s.Rota)` or `.Include(s => s.Shift.Rota)`).

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj`
Expected: Build succeeded (or fewer errors)

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Services/ShiftSignupService.cs
git commit -m "refactor: remove IsActive guards, replace shift.Title with Rota.Name in ShiftSignupService"
```

---

### Task 11: Delete SignupGarbageCollectionJob

**Files:**
- Delete: `src/Humans.Infrastructure/Jobs/SignupGarbageCollectionJob.cs`
- Modify: `src/Humans.Web/Extensions/RecurringJobExtensions.cs:61-62`

- [ ] **Step 1: Remove Hangfire registration**

In `src/Humans.Web/Extensions/RecurringJobExtensions.cs`, remove the line (around line 61-62):

```csharp
RecurringJob.AddOrUpdate<SignupGarbageCollectionJob>(
    "signup-garbage-collection",
    job => job.ExecuteAsync(CancellationToken.None),
    "0 4 * * *");
```

- [ ] **Step 2: Remove DI registration**

In `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`, remove `services.AddScoped<SignupGarbageCollectionJob>();` (around line 98).

- [ ] **Step 3: Delete the job file**

Delete `src/Humans.Infrastructure/Jobs/SignupGarbageCollectionJob.cs`

- [ ] **Step 4: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Errors only in views/controllers now

- [ ] **Step 5: Commit**

```bash
git add -u src/Humans.Infrastructure/Jobs/SignupGarbageCollectionJob.cs src/Humans.Web/Extensions/RecurringJobExtensions.cs src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs
git commit -m "refactor: remove SignupGarbageCollectionJob (deactivation removed)"
```

---

### Task 12: Update ShiftAdminController — remove deactivation, add delete, fix Title/IsActive

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftAdminController.cs`

- [ ] **Step 1: Remove DeactivateRota and DeactivateShift actions**

Delete the `DeactivateRota` and `DeactivateShift` action methods.

- [ ] **Step 2: Add DeleteRota and DeleteShift actions**

Create new POST actions to replace deactivation:

```csharp
[HttpPost("Rotas/{rotaId}/Delete")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DeleteRota(string slug, Guid rotaId)
{
    var (team, userId) = await ResolveTeamAndUserAsync(slug);
    if (team == null || userId == null) return NotFound();
    if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

    try
    {
        await _shiftMgmt.DeleteRotaAsync(rotaId);
        TempData["SuccessMessage"] = "Rota deleted.";
    }
    catch (InvalidOperationException ex)
    {
        TempData["ErrorMessage"] = ex.Message;
    }
    return RedirectToAction(nameof(Index), new { slug });
}

[HttpPost("Shifts/{shiftId}/Delete")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DeleteShift(string slug, Guid shiftId)
{
    var (team, userId) = await ResolveTeamAndUserAsync(slug);
    if (team == null || userId == null) return NotFound();
    if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

    try
    {
        await _shiftMgmt.DeleteShiftAsync(shiftId);
        TempData["SuccessMessage"] = "Shift deleted.";
    }
    catch (InvalidOperationException ex)
    {
        TempData["ErrorMessage"] = ex.Message;
    }
    return RedirectToAction(nameof(Index), new { slug });
}
```

- [ ] **Step 3: Remove all IsActive references in the controller**

Search for `.IsActive` in the file. Remove:
- `rota.IsActive = model.IsActive;` in EditRota
- `shift.IsActive = model.IsActive;` in EditShift
- `.Where(s => s.IsActive)` filter at line 69 in Index action
- Any other `IsActive` references

- [ ] **Step 4: Replace all `.Title` references (including `model.Title`)**

Search for `.Title` in the controller. Replace ALL matches — both `shift.Title` AND `model.Title`:
- `shift.Title` → `shift.Rota.Name`
- `model.Title` in TempData messages (lines 215, 254) → remove the title from the message (e.g., `"Shift created."` instead of `$"Shift '{model.Title}' created."`)

Ensure rota is eagerly loaded in any queries used.

- [ ] **Step 5: Update CreateRota action**

Add `Period` and `PracticalInfo` binding from the form:

```csharp
rota.Period = model.Period;
rota.PracticalInfo = model.PracticalInfo;
```

Add validation: if `Period == Build` and `eventSettings.BuildStartOffset >= 0`, return error "Cannot create a build rota — no build period is configured." Similarly for Strike when `EventEndOffset >= StrikeEndOffset`.

- [ ] **Step 6: Update EditRota action**

Add `Period` and `PracticalInfo` binding. Remove `IsActive` binding.

- [ ] **Step 7: Update CreateShift action**

Remove `Title` from shift creation.

- [ ] **Step 8: Update EditShift action**

Remove `Title` binding.

- [ ] **Step 9: Build to verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Errors only in views now

- [ ] **Step 10: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftAdminController.cs
git commit -m "refactor: update ShiftAdminController — delete replaces deactivate, remove Title/IsActive"
```

---

### Task 13: Update ShiftAdmin view

**Files:**
- Modify: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`

- [ ] **Step 1: Remove deactivate buttons**

Remove the Deactivate forms for both rotas (lines 101-107) and shifts. Replace with Delete buttons that include a confirmation dialog:

```html
<button type="button" class="btn btn-sm btn-outline-danger"
        onclick="if(confirm('Delete this rota and all its shifts?')) this.closest('form').submit()">
    Delete
</button>
```

Wire to `DeleteRota`/`DeleteShift` actions instead of `DeactivateRota`/`DeactivateShift`.

- [ ] **Step 2: Replace ALL shift.Title and signup.Shift.Title references**

Search for `.Title` in the entire view file. Replace every match:
- Line 61: `@signup.Shift.Title` in the pending approvals table → `@signup.Shift.Rota.Name`
- Line 156: `<th>Title</th>` table header → `<th>Shift</th>`
- Line 177: `@shift.Title` in shift table row → time/date display:

```csharp
@{
    var shiftLabel = shift.IsAllDay
        ? shiftDateLabel
        : $"{shift.StartTime}–{shift.StartTime.PlusHours((long)shift.Duration.TotalHours)}";
}
@shiftLabel
```

The rota name is already displayed in the card header, so the shift row just needs the time/date.

- [ ] **Step 3: Remove Title from BOTH edit AND create shift forms**

- Remove the Title input field AND its label from the inline **edit** shift form (around line 238 — remove the entire `<div class="col-md-3">` containing the Title input)
- Remove the Title input field AND its label from the **create** shift form (around line 362 — remove the entire `<div class="col-md-3">` containing the Title input)

- [ ] **Step 4: Remove IsActive references**

Remove `@(!shift.IsActive ? "table-secondary" : "")` class conditionals.
Remove any `IsActive` checkboxes from edit forms.

- [ ] **Step 5: Add Period dropdown to Create Rota form**

In the Create Rota form section, add a Period dropdown:

```html
<div class="col-md-2">
    <label class="form-label">Period</label>
    <select name="Period" class="form-select">
        <option value="Build">Build</option>
        <option value="Event" selected>Event</option>
        <option value="Strike">Strike</option>
    </select>
</div>
```

- [ ] **Step 6: Add PracticalInfo to Create Rota form**

Use `<textarea>` not `<input>` — PracticalInfo is up to 2000 chars of multi-line text (meeting points, instructions):

```html
<div class="col-md-3">
    <label class="form-label">Practical Info</label>
    <textarea name="PracticalInfo" class="form-control" rows="3" placeholder="Meeting point, instructions..."></textarea>
</div>
```

- [ ] **Step 7: Build to verify**

Run: `dotnet build Humans.slnx`
Expected: Fewer errors, may still have view compilation issues

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Web/Views/ShiftAdmin/Index.cshtml
git commit -m "refactor: update ShiftAdmin view for v2 (delete, no title, period)"
```

---

### Task 14: Update ALL views — replace shift.Title, remove IsActive references

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml`
- Modify: `src/Humans.Web/Views/Shifts/Mine.cshtml`
- Modify: `src/Humans.Web/Views/Shared/_ShiftCards.cshtml`
- Modify: `src/Humans.Web/Views/Home/_ShiftCards.cshtml`
- Modify: `src/Humans.Web/Views/ShiftDashboard/Index.cshtml`
- Modify: `src/Humans.Web/Views/Profile/Index.cshtml` (NoShowHistoryItem.ShiftTitle → ShiftLabel)

- [ ] **Step 1: Full grep for .Title in all view files**

Run: `grep -rn "\.Title" src/Humans.Web/Views/ --include="*.cshtml" | grep -i shift`

Fix every match. Use the display format from design spec section 4.2:
- **Event-time shifts:** `"{Rota.Name} — {StartTime}–{EndTime}"`
- **All-day shifts:** `"{Rota.Name} — {Date}"`

In **nested** views where the rota name is already shown as a section header (e.g., `Shifts/Index.cshtml` groups shifts under rota headings), show only the time/date portion to avoid redundancy.

In **flat** views (Mine, ShiftCards, Dashboard) where shifts are listed without rota headers, show the full format.

Key locations:

- `Shifts/Index.cshtml:137` — nested under rota header, replace `shift.Title` with time/date only (e.g., `@shift.StartTime–@shift.StartTime.PlusHours((long)shift.Duration.TotalHours)` or date for all-day)
- `Shifts/Mine.cshtml:80,118,155` — flat list, replace `shift.Title` with full label: `@(shift.IsAllDay ? $"{shift.Rota.Name} — {dateLabel}" : $"{shift.Rota.Name} — {shift.StartTime}–...")`
- `Shared/_ShiftCards.cshtml` — flat list, replace with full label (same pattern, use IsAllDay-aware format)
- `Home/_ShiftCards.cshtml:17,39` — flat list, replace with full label (same pattern)
- `ShiftDashboard/Index.cshtml:104,131` — flat list, replace with full label
- `Profile/Index.cshtml:80` — replace `item.ShiftTitle` with `item.ShiftLabel`

Ensure all controller/service queries that feed these views include `.Include(s => s.Rota)`.

- [ ] **Step 2: Remove IsActive references in views**

Search for `IsActive` in all `.cshtml` files and remove class conditionals like `@(!shift.IsActive ? "table-secondary" : "")`.

- [ ] **Step 3: Build the full solution**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/
git commit -m "refactor: replace shift.Title with rota name in all views, remove IsActive refs"
```

---

### Task 15: Update remaining controllers for Title/IsActive removal

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`
- Modify: `src/Humans.Web/Controllers/HumanController.cs`
- Modify: `src/Humans.Web/Controllers/HomeController.cs` (if it assembles shift card data)

- [ ] **Step 1: Full grep for .Title and .IsActive in all controllers**

Run: `grep -rn "\.Title\|\.IsActive" src/Humans.Web/Controllers/ --include="*.cs" | grep -i shift`

Fix every match. Key locations:
- `ShiftsController.cs` — replace all `shift.Title` with `shift.Rota.Name`
- `HumanController.cs:98` — replace `ShiftTitle = s.Shift.Title` with `ShiftLabel = s.Shift.Rota.Name`
- Ensure all queries include `.Include(s => s.Rota)` so `shift.Rota.Name` is available

- [ ] **Step 2: Build and verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/
git commit -m "refactor: remove Title/IsActive references from all controllers"
```

---

### Task 16: Create EF migration

**Files:**
- Create: `src/Humans.Infrastructure/Migrations/YYYYMMDD_ShiftV2Schema.cs` (auto-generated — note: Migrations/ is directly under Infrastructure/, not under Data/)

- [ ] **Step 1: Generate migration**

Run: `dotnet ef migrations add ShiftV2Schema --project src/Humans.Infrastructure --startup-project src/Humans.Web`

- [ ] **Step 2: Review the generated migration**

Verify it includes:
- Drop `shifts.title` column
- Drop `shifts.is_active` column
- Drop `rotas.is_active` column
- Add `shifts.is_all_day` column (bool, default false)
- Add `shift_signups.signup_block_id` column (Guid?, nullable)
- Add `rotas.period` column (string, default 'Event')
- Add `rotas.practical_info` column (string?, nullable)
- Add `team_role_definitions.period` column (string, default 'YearRound')
- Create `general_availability` table

- [ ] **Step 3: Add pre-migration cleanup SQL**

In the migration `Up()` method, BEFORE the column drops, add SQL to clean up deactivated records.

**Important:** Do NOT delete shifts/rotas that have ANY signup history (including Bailed/NoShow) — these records are used for no-show history display. Only delete truly empty inactive records. For anything with signups, just force `is_active = true` so the data is preserved.

```csharp
migrationBuilder.Sql(@"
    -- Cancel pending signups on inactive shifts (before re-activating them)
    UPDATE shift_signups SET status = 'Cancelled', updated_at = now()
    WHERE status = 'Pending'
    AND shift_id IN (SELECT id FROM shifts WHERE is_active = false);

    -- Delete inactive shifts with zero signups (completely empty)
    DELETE FROM shifts WHERE is_active = false
        AND id NOT IN (SELECT DISTINCT shift_id FROM shift_signups);

    -- Force remaining inactive shifts to active (they have historical signups to preserve)
    UPDATE shifts SET is_active = true WHERE is_active = false;

    -- Delete inactive rotas with no remaining child shifts
    DELETE FROM rotas WHERE is_active = false
        AND id NOT IN (SELECT DISTINCT rota_id FROM shifts);

    -- Force remaining inactive rotas to active
    UPDATE rotas SET is_active = true WHERE is_active = false;
");
```

- [ ] **Step 4: Apply migration locally**

Run: `dotnet ef database update --project src/Humans.Infrastructure --startup-project src/Humans.Web`

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Data/Migrations/
git commit -m "feat: add ShiftV2Schema migration"
```

---

### Task 17: Update existing tests for Slice A

**Files:**
- Modify: `tests/Humans.Domain.Tests/Entities/ShiftTests.cs`
- Modify: `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs`
- Modify: `tests/Humans.Application.Tests/Services/ShiftUrgencyTests.cs`
- Modify: `tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs`

Note: `tests/Humans.Application.Tests/Services/ShiftManagementServiceTests.cs` does NOT exist yet — it will be created in Task 18.

- [ ] **Step 1: Fix ShiftTests**

Remove/update any test that sets `Title` on a Shift. Update shift construction in all tests to omit Title. Remove any tests that check `IsActive`.

- [ ] **Step 2: Fix ShiftSignupServiceTests**

- **Delete** the `SignUp_InactiveShift_ReturnsError` test entirely (it tests deactivation behavior being removed). Delete any similar `InactiveRota` test if one exists.
- Update the `SeedShift` helper method signature: remove the `string title = "Test Shift"` parameter and the `Title = title` assignment inside the method body.
- Remove `IsActive = true` from all shift/rota construction in `SeedShift`, `SeedShiftScenario`, and test setup methods.
- Ensure rotas have `Period = RotaPeriod.Event` set in test construction.

- [ ] **Step 3: Verify ShiftUrgencyTests**

Confirm no `Title` or `IsActive` references exist in `ShiftUrgencyTests.cs` (none expected — the `MakeShift` helper does not set these). If any are found, remove them.

- [ ] **Step 4: Add new enums to EnumStringStabilityTests**

In `tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs`, add entries for the new string-stored enums in `StringStoredEnumData`:
- `RotaPeriod`: `["Build", "Event", "Strike"]`
- `RolePeriod`: `["YearRound", "Build", "Event", "Strike"]`

This guards against accidental renames that would break database values.

- [ ] **Step 5: Add test for delete-with-confirmed-signups guard**

Add to `ShiftSignupServiceTests` or create a new test verifying that `DeleteRotaAsync` throws `InvalidOperationException` when a child shift has confirmed signups.

- [ ] **Step 6: Run all tests**

Run: `dotnet test Humans.slnx`
Expected: All tests pass

- [ ] **Step 7: Commit**

```bash
git add tests/
git commit -m "test: update existing tests for shift v2 schema changes"
```

---

## Chunk 2: Build/Strike Shifts (Slice B)

### Task 18: Write tests for build/strike bulk shift creation

**Files:**
- Create or modify: `tests/Humans.Application.Tests/Services/ShiftManagementServiceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task CreateBuildStrikeShifts_CreatesOneAllDayShiftPerDay()
{
    // Arrange: rota with Period=Build, staffing grid for days -3 to -1
    // Act: call CreateBuildStrikeShiftsAsync
    // Assert: 3 shifts created, all IsAllDay=true, correct DayOffsets, correct Min/Max
}

[Fact]
public async Task CreateBuildStrikeShifts_SetsCorrectMinMaxPerDay()
{
    // Arrange: staffing grid with varying min/max per day
    // Act: call CreateBuildStrikeShiftsAsync
    // Assert: each shift has the min/max from its corresponding day in the grid
}

[Fact]
public async Task CreateBuildStrikeShifts_RejectsEventPeriodRota()
{
    // Arrange: rota with Period=Event
    // Act + Assert: throws InvalidOperationException
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Humans.slnx --filter "CreateBuildStrikeShifts"`
Expected: FAIL — method does not exist

- [ ] **Step 3: Commit failing tests**

```bash
git add tests/
git commit -m "test: add failing tests for build/strike bulk shift creation"
```

---

### Task 19: Implement CreateBuildStrikeShiftsAsync

**Files:**
- Modify: `src/Humans.Application/Interfaces/IShiftManagementService.cs`
- Modify: `src/Humans.Infrastructure/Services/ShiftManagementService.cs`

- [ ] **Step 1: Add method to interface**

```csharp
Task CreateBuildStrikeShiftsAsync(Guid rotaId, Dictionary<int, (int Min, int Max)> dailyStaffing);
```

- [ ] **Step 2: Implement in service**

```csharp
public async Task CreateBuildStrikeShiftsAsync(Guid rotaId, Dictionary<int, (int Min, int Max)> dailyStaffing)
{
    var rota = await GetRotaByIdAsync(rotaId);
    if (rota == null) throw new InvalidOperationException("Rota not found");
    if (rota.Period == RotaPeriod.Event)
        throw new InvalidOperationException("Build/strike shift generation is only for Build or Strike rotas");

    var eventSettings = await GetActiveAsync();
    if (eventSettings == null) throw new InvalidOperationException("No active event");

    // Validate day offsets are within the correct period range
    foreach (var dayOffset in dailyStaffing.Keys)
    {
        if (rota.Period == RotaPeriod.Build && (dayOffset < eventSettings.BuildStartOffset || dayOffset >= 0))
            throw new InvalidOperationException($"Day offset {dayOffset} is outside the build period ({eventSettings.BuildStartOffset} to -1)");
        if (rota.Period == RotaPeriod.Strike && (dayOffset <= eventSettings.EventEndOffset || dayOffset > eventSettings.StrikeEndOffset))
            throw new InvalidOperationException($"Day offset {dayOffset} is outside the strike period ({eventSettings.EventEndOffset + 1} to {eventSettings.StrikeEndOffset})");
    }

    var clock = _clock.GetCurrentInstant();

    foreach (var (dayOffset, staffing) in dailyStaffing.OrderBy(d => d.Key))
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rotaId,
            IsAllDay = true,
            DayOffset = dayOffset,
            StartTime = new LocalTime(0, 0),
            Duration = Duration.FromHours(24),
            MinVolunteers = staffing.Min,
            MaxVolunteers = staffing.Max,
            CreatedAt = clock,
            UpdatedAt = clock
        };
        _dbContext.Shifts.Add(shift);
    }

    await _dbContext.SaveChangesAsync();
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test Humans.slnx --filter "CreateBuildStrikeShifts"`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/IShiftManagementService.cs src/Humans.Infrastructure/Services/ShiftManagementService.cs
git commit -m "feat: implement CreateBuildStrikeShiftsAsync for bulk all-day shift creation"
```

---

### Task 20: Write tests for date-range signup

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task SignUpRange_CreatesOneSignupPerDay()
{
    // Arrange: rota with 5 all-day shifts (days -5 to -1), user picks days -3 to -1
    // Act: SignUpRangeAsync
    // Assert: 3 ShiftSignup records created, all share same SignupBlockId
}

[Fact]
public async Task SignUpRange_BlocksIfAnyDayOverlaps()
{
    // Arrange: user already has a confirmed signup on day -2
    // Act: try to SignUpRangeAsync for days -3 to -1
    // Assert: fails with overlap error mentioning day -2
}

[Fact]
public async Task BailRange_BailsAllSignupsInBlock()
{
    // Arrange: user signed up for days -3 to -1 (shared SignupBlockId)
    // Act: BailRangeAsync with the SignupBlockId
    // Assert: all 3 signups now Bailed
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Humans.slnx --filter "SignUpRange|BailRange"`
Expected: FAIL

- [ ] **Step 3: Commit**

```bash
git add tests/
git commit -m "test: add failing tests for date-range signup and bail"
```

---

### Task 21: Implement SignUpRangeAsync and BailRangeAsync

**Files:**
- Modify: `src/Humans.Application/Interfaces/IShiftSignupService.cs`
- Modify: `src/Humans.Infrastructure/Services/ShiftSignupService.cs`

- [ ] **Step 1: Add methods to interface**

```csharp
Task<SignupResult> SignUpRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid? actorUserId = null);
Task BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null);
```

- [ ] **Step 2: Implement SignUpRangeAsync**

Key logic:
- Find all all-day shifts in the rota within the day offset range
- Check overlap for each day against existing confirmed signups
- If any overlap, return `SignupResult.Fail` with the conflicting days listed
- Generate a shared `SignupBlockId = Guid.NewGuid()`
- Create one `ShiftSignup` per shift, all sharing the `SignupBlockId`
- Auto-confirm for Public policy, Pending for RequireApproval
- Audit log each signup

- [ ] **Step 3: Implement BailRangeAsync**

Key logic:
- Find all signups with the given `SignupBlockId`
- Verify the actor has permission to bail (same user, or coordinator/admin)
- EE freeze check: if any shift is build-period and past `EarlyEntryClose`, block for non-privileged users
- Call `signup.Bail(actorUserId, clock, reason)` on each
- Audit log each bail

- [ ] **Step 4: Run tests**

Run: `dotnet test Humans.slnx --filter "SignUpRange|BailRange"`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/IShiftSignupService.cs src/Humans.Infrastructure/Services/ShiftSignupService.cs
git commit -m "feat: implement SignUpRangeAsync and BailRangeAsync for build/strike"
```

---

### Task 22: Build/Strike rota creation UI — staffing grid

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftAdminController.cs`
- Modify: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`
- Create: `src/Humans.Web/Models/StaffingGridModel.cs` (or add to ShiftViewModels.cs)

- [ ] **Step 1: Add StaffingGrid view model**

```csharp
public class StaffingGridModel
{
    public Guid RotaId { get; set; }
    public List<DayStaffingEntry> Days { get; set; } = [];
}

public class DayStaffingEntry
{
    public int DayOffset { get; set; }
    public int MinVolunteers { get; set; } = 2;
    public int MaxVolunteers { get; set; } = 5;
}
```

- [ ] **Step 2: Add ConfigureStaffing POST action to ShiftAdminController**

```csharp
[HttpPost("Rotas/{rotaId}/ConfigureStaffing")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ConfigureStaffing(string slug, Guid rotaId, StaffingGridModel model)
```

This calls `_shiftMgmt.CreateBuildStrikeShiftsAsync(rotaId, dailyStaffing)`.

- [ ] **Step 3: Add staffing grid UI to the view**

After a Build/Strike rota is created (or on the existing rota card), show a collapsible staffing grid form with one row per day in the period range. Each row has: date label, min input, max input.

- [ ] **Step 4: Build and manually test**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftAdminController.cs src/Humans.Web/Views/ShiftAdmin/Index.cshtml src/Humans.Web/Models/
git commit -m "feat: add build/strike staffing grid UI for rota creation"
```

---

### Task 23: Build/Strike signup UI — date-range picker

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml`
- Modify: `src/Humans.Web/Views/Shifts/Mine.cshtml`

- [ ] **Step 1: Update browse page for build/strike rotas**

Build/strike rotas display differently: show the date range, per-day fill rates, and a start/end date picker for signup instead of individual shift signup buttons.

- [ ] **Step 2: Add SignUpRange POST action to ShiftsController**

```csharp
[HttpPost("SignUpRange")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SignUpRange(Guid rotaId, int startDayOffset, int endDayOffset)
```

Calls `_signupService.SignUpRangeAsync(userId, rotaId, startDayOffset, endDayOffset)`.

- [ ] **Step 3: Add BailRange POST action to ShiftsController**

```csharp
[HttpPost("BailRange")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> BailRange(Guid signupBlockId)
```

- [ ] **Step 4: Add BailRange POST action to ShiftAdminController**

Coordinators also need to bail entire build/strike ranges for volunteers:

```csharp
[HttpPost("BailRange")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> BailRange(string slug, Guid signupBlockId, string? reason)
```

- [ ] **Step 5: Update Mine view for range display and bail**

Group signups by `SignupBlockId` when non-null. Display as "Rota Name — Date to Date" with a single Bail button per block.

The view needs to distinguish range bail from individual bail:
```html
@if (signup.SignupBlockId != null)
{
    <form asp-action="BailRange" method="post">
        @Html.AntiForgeryToken()
        <input type="hidden" name="signupBlockId" value="@signup.SignupBlockId" />
        <button type="submit" class="btn btn-sm btn-outline-warning">Bail</button>
    </form>
}
else
{
    <form asp-action="Bail" method="post">
        @Html.AntiForgeryToken()
        <input type="hidden" name="signupId" value="@signup.Id" />
        <button type="submit" class="btn btn-sm btn-outline-warning">Bail</button>
    </form>
}
```

Note: All-day overlap blocking is intentional — a volunteer cannot be on two all-day rotas on the same day since all-day shifts span 00:00–24:00 and the overlap check compares absolute time ranges.

- [ ] **Step 6: Build and manually test**

Run: `dotnet build Humans.slnx && dotnet test Humans.slnx`
Expected: Build succeeded, all tests pass

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftsController.cs src/Humans.Web/Controllers/ShiftAdminController.cs src/Humans.Web/Views/Shifts/
git commit -m "feat: add build/strike date-range signup and bail UI"
```

---

## Chunk 3: Event Rota Bulk Generation (Slice C)

### Task 24: Write tests for Cartesian product shift generation

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/ShiftManagementServiceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task GenerateEventShifts_CreatesCartesianProduct()
{
    // Arrange: event rota, days 0-2, slots [(08:00, 4h), (14:00, 4h)]
    // Act: GenerateEventShiftsAsync
    // Assert: 6 shifts created (3 days × 2 slots), none IsAllDay
}

[Fact]
public async Task GenerateEventShifts_RejectsBuildPeriodRota()
{
    // Arrange: rota with Period=Build
    // Act + Assert: throws InvalidOperationException
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Humans.slnx --filter "GenerateEventShifts"`
Expected: FAIL

- [ ] **Step 3: Commit**

```bash
git add tests/
git commit -m "test: add failing tests for event rota bulk shift generation"
```

---

### Task 25: Implement GenerateEventShiftsAsync

**Files:**
- Modify: `src/Humans.Application/Interfaces/IShiftManagementService.cs`
- Modify: `src/Humans.Infrastructure/Services/ShiftManagementService.cs`

- [ ] **Step 1: Add to interface**

```csharp
Task GenerateEventShiftsAsync(Guid rotaId, int startDayOffset, int endDayOffset,
    List<(LocalTime StartTime, double DurationHours)> timeSlots, int minVolunteers = 2, int maxVolunteers = 5);
```

- [ ] **Step 2: Implement**

Cartesian product: for each day in range × each time slot, create a Shift with `IsAllDay = false`.

- [ ] **Step 3: Run tests**

Run: `dotnet test Humans.slnx --filter "GenerateEventShifts"`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/IShiftManagementService.cs src/Humans.Infrastructure/Services/ShiftManagementService.cs
git commit -m "feat: implement GenerateEventShiftsAsync (Cartesian product)"
```

---

### Task 26: Generate Shifts UI

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftAdminController.cs`
- Modify: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`

- [ ] **Step 1: Add GenerateShifts POST action**

```csharp
[HttpPost("Rotas/{rotaId}/GenerateShifts")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> GenerateShifts(string slug, Guid rotaId, GenerateEventShiftsModel model)
```

- [ ] **Step 2: Add Generate Shifts form to the view**

On event-period rota cards, add a collapsible "Generate Shifts" form with:
- Start Day / End Day dropdowns
- Dynamic time slot rows (Start Time + Duration hours) with add/remove buttons
- Min/Max volunteers inputs
- Submit button

Use JavaScript for dynamic time slot rows.

- [ ] **Step 3: Build and test**

Run: `dotnet build Humans.slnx && dotnet test Humans.slnx`
Expected: Build succeeded, all tests pass

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftAdminController.cs src/Humans.Web/Views/ShiftAdmin/Index.cshtml src/Humans.Web/Models/
git commit -m "feat: add Generate Shifts UI for event-period rotas"
```

---

## Chunk 4: General Volunteer Pool + Role Periods (Slices D & E)

### Task 27: General Availability service

**Files:**
- Create: `src/Humans.Application/Interfaces/IGeneralAvailabilityService.cs`
- Create: `src/Humans.Infrastructure/Services/GeneralAvailabilityService.cs`
- Create: `tests/Humans.Application.Tests/Services/GeneralAvailabilityServiceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task SetAvailability_CreatesRecord()
{
    // Assert: record created with correct day offsets
}

[Fact]
public async Task SetAvailability_UpdatesExistingRecord()
{
    // Assert: same record updated, not duplicated (unique constraint)
}

[Fact]
public async Task GetAvailableVolunteers_ReturnsMatchingDayOffset()
{
    // Assert: only volunteers available on the queried day are returned
}
```

- [ ] **Step 2: Define interface**

```csharp
public interface IGeneralAvailabilityService
{
    Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets);
    Task<GeneralAvailability?> GetByUserAsync(Guid userId, Guid eventSettingsId);
    Task<List<GeneralAvailability>> GetAvailableForDayAsync(Guid eventSettingsId, int dayOffset);
    Task DeleteAsync(Guid userId, Guid eventSettingsId);
}
```

- [ ] **Step 3: Implement service**

Standard CRUD against `GeneralAvailability` entity. For `GetAvailableForDayAsync`: EF Core's LINQ translation of `List<int>.Contains(int)` on jsonb columns may not generate the expected SQL. At ~500 users, the simplest approach is to load all `GeneralAvailability` records for the event and filter in memory: `.Where(ga => ga.AvailableDayOffsets.Contains(dayOffset))`. If this evaluates client-side, it's fine at this scale. Alternatively, use `EF.Functions.JsonContains()` if available in the Npgsql provider version.

- [ ] **Step 4: Register in DI**

Add to `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` (where all infrastructure services are registered, NOT in Program.cs):
`services.AddScoped<IGeneralAvailabilityService, GeneralAvailabilityService>();`

- [ ] **Step 5: Run tests**

Run: `dotnet test Humans.slnx --filter "GeneralAvailability"`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/IGeneralAvailabilityService.cs src/Humans.Infrastructure/Services/GeneralAvailabilityService.cs tests/ src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs
git commit -m "feat: add GeneralAvailabilityService for volunteer pool"
```

---

### Task 28: General Availability UI

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`
- Modify: `src/Humans.Web/Views/Shifts/Mine.cshtml` (or Index.cshtml)

- [ ] **Step 1: Add GET/POST actions for general availability**

On `/Shifts/Mine`, add a section where the volunteer can see and edit their available days.

- [ ] **Step 2: Add date grid UI**

Render a checkbox grid of all days in the event period. Pre-check days the user has already marked.

- [ ] **Step 3: Add pool indicator to voluntell search**

In `ShiftAdminController.SearchVolunteers` and `ShiftDashboardController.SearchVolunteers`, enrich results with a "Pool" flag when the volunteer has `GeneralAvailability` covering the shift's day.

- [ ] **Step 4: Build and test**

Run: `dotnet build Humans.slnx && dotnet test Humans.slnx`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/ src/Humans.Web/Views/
git commit -m "feat: add general availability UI and pool indicator in voluntell"
```

---

### Task 29: Role Period tags

**Files:**
- Modify: `src/Humans.Application/Interfaces/ITeamService.cs` (add Period parameter to CreateRoleDefinitionAsync / UpdateRoleDefinitionAsync)
- Modify: `src/Humans.Infrastructure/Services/TeamService.cs` (accept and persist Period)
- Modify: `src/Humans.Web/Models/TeamViewModels.cs` (add Period to `CreateRoleDefinitionModel`, `EditRoleDefinitionModel`, and `TeamRoleDefinitionViewModel`)
- Modify: `src/Humans.Web/Controllers/TeamAdminController.cs` (bind Period in `CreateRole` and `EditRole` actions)
- Modify: `src/Humans.Web/Views/TeamAdmin/Roles.cshtml` (add Period dropdown to role create/edit forms)
- Modify: `src/Humans.Web/Views/Team/Roster.cshtml` (add period filter dropdown)

- [ ] **Step 1: Update ITeamService interface**

Add `RolePeriod period` parameter to `CreateRoleDefinitionAsync` and `UpdateRoleDefinitionAsync` in `src/Humans.Application/Interfaces/ITeamService.cs`.

- [ ] **Step 2: Update TeamService implementation**

In `src/Humans.Infrastructure/Services/TeamService.cs`, accept and persist `period` in both `CreateRoleDefinitionAsync` and `UpdateRoleDefinitionAsync`.

- [ ] **Step 3: Update view models**

In `src/Humans.Web/Models/TeamViewModels.cs`:
- Add `RolePeriod Period { get; set; } = RolePeriod.YearRound;` to both `CreateRoleDefinitionModel` and `EditRoleDefinitionModel`
- Add `RolePeriod Period { get; set; }` to `TeamRoleDefinitionViewModel` and map it in `FromEntity()`

- [ ] **Step 4: Update TeamAdminController**

In `src/Humans.Web/Controllers/TeamAdminController.cs`, pass `model.Period` to the service in both `CreateRole` and `EditRole` actions.

- [ ] **Step 5: Add Period dropdown to role forms**

In `src/Humans.Web/Views/TeamAdmin/Roles.cshtml`, add a Period dropdown (YearRound selected by default) to the create and edit forms.

- [ ] **Step 6: Add Period filter to roster page**

In `src/Humans.Web/Views/Team/Roster.cshtml`, add a dropdown filter: "All Periods" / "Year-round" / "Build" / "Event" / "Strike". Filter the role list client-side or via query parameter.

- [ ] **Step 7: Build and test**

Run: `dotnet build Humans.slnx && dotnet test Humans.slnx`
Expected: All pass

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/Interfaces/ITeamService.cs src/Humans.Infrastructure/Services/TeamService.cs src/Humans.Web/Models/TeamViewModels.cs src/Humans.Web/Controllers/TeamAdminController.cs src/Humans.Web/Views/TeamAdmin/ src/Humans.Web/Views/Team/
git commit -m "feat: add role period tags and roster filter"
```

---

### Task 30: Practical Info display

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml`
- Modify: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`

- [ ] **Step 1: Display practical info on shift browse page**

When viewing a rota's shifts on `/Shifts`, show the `PracticalInfo` text if present (below the rota description).

- [ ] **Step 2: Display and edit practical info on ShiftAdmin**

On the rota card in `/Teams/{slug}/Shifts`, show `PracticalInfo` and include it in the edit form.

- [ ] **Step 3: Build and test**

Run: `dotnet build Humans.slnx && dotnet test Humans.slnx`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/
git commit -m "feat: display and edit practical info on rotas"
```

---

## Chunk 5: Documentation & Final Verification

### Task 31: Update documentation

**Files:**
- Modify: `docs/features/25-shift-management.md`
- Modify: `.claude/DATA_MODEL.md`

- [ ] **Step 1: Update feature doc**

Update `docs/features/25-shift-management.md` to reflect:
- Shift.Title removed
- Shift.IsAllDay added
- Rota.Period added (RotaPeriod enum)
- Rota.PracticalInfo added
- Rota/Shift.IsActive removed (delete only)
- ShiftSignup.SignupBlockId added
- GeneralAvailability entity added
- TeamRoleDefinition.Period added (RolePeriod enum)
- New service methods: CreateBuildStrikeShiftsAsync, GenerateEventShiftsAsync, SignUpRangeAsync, BailRangeAsync
- GC job removed
- Updated routes/flows for build/strike

- [ ] **Step 2: Update data model doc**

Update `.claude/DATA_MODEL.md` with all entity changes.

- [ ] **Step 3: Commit**

```bash
git add docs/features/25-shift-management.md .claude/DATA_MODEL.md
git commit -m "docs: update shift management docs for v2 changes"
```

---

### Task 32: Final verification

- [ ] **Step 1: Run full test suite**

Run: `dotnet test Humans.slnx`
Expected: All tests pass

- [ ] **Step 2: Run full build**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded, 0 warnings (or only pre-existing warnings)

- [ ] **Step 3: Verify all pages are reachable**

Manually check in QA:
- `/Shifts` — browse page loads, build/strike rotas show date ranges
- `/Shifts/Mine` — my shifts page loads, range signups grouped correctly
- `/Teams/{slug}/Shifts` — ShiftAdmin loads, Period dropdown on Create Rota, staffing grid works, Generate Shifts works, Delete buttons work
- `/Shifts/Dashboard` — dashboard loads, pool indicator shows in voluntell search
- `/Teams/{slug}/Roster` — period filter works

- [ ] **Step 4: Push to QA**

Run: `./deploy-qa.sh`

- [ ] **Step 5: Final commit if any fixes needed**

```bash
git add -A
git commit -m "fix: final QA fixes for shift v2"
```
