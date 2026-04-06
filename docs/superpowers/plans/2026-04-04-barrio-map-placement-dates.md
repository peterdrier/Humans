# Barrio Map Placement Dates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two optional `LocalDateTime` fields to `CampMapSettings` so admins can set informational open/close dates for the placement phase, displayed in the help modal.

**Architecture:** Add fields to the domain entity + EF migration, expose a service method, add a controller action + admin form, and render the dates in the help modal partial.

**Tech Stack:** ASP.NET Core MVC, EF Core, NodaTime (`LocalDateTime`), Bootstrap 5.

---

## Files

| File | Action |
|------|--------|
| `src/Humans.Domain/Entities/CampMapSettings.cs` | Modify — add two `LocalDateTime?` fields |
| `src/Humans.Application/Interfaces/ICampMapService.cs` | Modify — add `UpdatePlacementDatesAsync` |
| `src/Humans.Infrastructure/Services/CampMapService.cs` | Modify — implement `UpdatePlacementDatesAsync` |
| `src/Humans.Infrastructure/Migrations/<timestamp>_AddPlacementDatesToCampMapSettings.cs` | Create — EF migration |
| `src/Humans.Web/Controllers/BarrioMapController.cs` | Modify — add `UpdatePlacementDates` action, pass dates in `Index` |
| `src/Humans.Web/Views/BarrioMap/Admin.cshtml` | Modify — add placement dates form card |
| `src/Humans.Web/Views/BarrioMap/_PlacementHelpModal.cshtml` | Modify — display dates in "When is it?" section |
| `tests/Humans.Application.Tests/Services/CampMapServiceTests.cs` | Modify — add `UpdatePlacementDatesAsync` tests |

---

### Task 1: Add fields to domain entity + interface

**Files:**
- Modify: `src/Humans.Domain/Entities/CampMapSettings.cs`
- Modify: `src/Humans.Application/Interfaces/ICampMapService.cs`

- [ ] **Step 1: Add fields to `CampMapSettings`**

In `src/Humans.Domain/Entities/CampMapSettings.cs`, add after `ClosedAt`:

```csharp
public LocalDateTime? PlacementOpensAt { get; set; }
public LocalDateTime? PlacementClosesAt { get; set; }
```

Full file after change:

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class CampMapSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The season year this row applies to. Unique.</summary>
    public int Year { get; init; }

    public bool IsPlacementOpen { get; set; }
    public Instant? OpenedAt { get; set; }
    public Instant? ClosedAt { get; set; }

    /// <summary>Informational scheduled open time shown in help modal. Not enforced.</summary>
    public LocalDateTime? PlacementOpensAt { get; set; }

    /// <summary>Informational scheduled close time shown in help modal. Not enforced.</summary>
    public LocalDateTime? PlacementClosesAt { get; set; }

    /// <summary>GeoJSON FeatureCollection defining the visual site boundary. Null until uploaded.</summary>
    public string? LimitZoneGeoJson { get; set; }

    public Instant UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Add method to interface**

In `src/Humans.Application/Interfaces/ICampMapService.cs`, add after `DeleteLimitZoneAsync`:

```csharp
Task UpdatePlacementDatesAsync(LocalDateTime? opensAt, LocalDateTime? closesAt, CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build Humans.slnx
```

Expected: build succeeds (with one "not implemented" error from `CampMapService` if the interface has abstract methods — that's fine until Task 2).

---

### Task 2: Implement service method + write tests

**Files:**
- Modify: `src/Humans.Infrastructure/Services/CampMapService.cs`
- Modify: `tests/Humans.Application.Tests/Services/CampMapServiceTests.cs`

- [ ] **Step 1: Write failing tests**

In `CampMapServiceTests.cs`, add after the last test method:

```csharp
[Fact]
public async Task UpdatePlacementDatesAsync_SetsBothDates()
{
    await SeedMapSettingsAsync();
    var opens = new LocalDateTime(2026, 4, 10, 18, 0);
    var closes = new LocalDateTime(2026, 4, 20, 23, 59);

    await _sut.UpdatePlacementDatesAsync(opens, closes);

    var settings = await _dbContext.CampMapSettings.SingleAsync();
    settings.PlacementOpensAt.Should().Be(opens);
    settings.PlacementClosesAt.Should().Be(closes);
}

[Fact]
public async Task UpdatePlacementDatesAsync_ClearsDates_WhenNull()
{
    await SeedMapSettingsAsync();
    var settings = await _dbContext.CampMapSettings.SingleAsync();
    settings.PlacementOpensAt = new LocalDateTime(2026, 4, 10, 18, 0);
    settings.PlacementClosesAt = new LocalDateTime(2026, 4, 20, 23, 59);
    await _dbContext.SaveChangesAsync();

    await _sut.UpdatePlacementDatesAsync(null, null);

    var updated = await _dbContext.CampMapSettings.SingleAsync();
    updated.PlacementOpensAt.Should().BeNull();
    updated.PlacementClosesAt.Should().BeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/Humans.Application.Tests --filter "UpdatePlacementDatesAsync"
```

Expected: compile error or `NotImplementedException`.

- [ ] **Step 3: Implement `UpdatePlacementDatesAsync` in service**

In `src/Humans.Infrastructure/Services/CampMapService.cs`, add after `DeleteLimitZoneAsync`:

```csharp
public async Task UpdatePlacementDatesAsync(LocalDateTime? opensAt, LocalDateTime? closesAt, CancellationToken cancellationToken = default)
{
    var settings = await GetSettingsAsync(cancellationToken);
    settings.PlacementOpensAt = opensAt;
    settings.PlacementClosesAt = closesAt;
    settings.UpdatedAt = _clock.GetCurrentInstant();
    await _dbContext.SaveChangesAsync(cancellationToken);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Humans.Application.Tests --filter "UpdatePlacementDatesAsync"
```

Expected: 2 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain/Entities/CampMapSettings.cs \
        src/Humans.Application/Interfaces/ICampMapService.cs \
        src/Humans.Infrastructure/Services/CampMapService.cs \
        tests/Humans.Application.Tests/Services/CampMapServiceTests.cs
git commit -m "feat: add informational placement dates to CampMapSettings"
```

---

### Task 3: EF Core migration

**Files:**
- Create: `src/Humans.Infrastructure/Migrations/<timestamp>_AddPlacementDatesToCampMapSettings.cs`

- [ ] **Step 1: Generate migration**

```bash
dotnet ef migrations add AddPlacementDatesToCampMapSettings \
  --project src/Humans.Infrastructure \
  --startup-project src/Humans.Web
```

Expected: new migration file created under `src/Humans.Infrastructure/Migrations/`.

- [ ] **Step 2: Verify migration content**

Open the generated migration. It should contain two `AddColumn` calls for `placement_opens_at` and `placement_closes_at`, both `timestamp without time zone`, nullable. No other changes.

- [ ] **Step 3: Run EF migration reviewer**

As per CLAUDE.md, run the EF migration reviewer agent (`.claude/agents/ef-migration-reviewer.md`) against the new migration before committing.

- [ ] **Step 4: Commit migration**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat: migration — add placement_opens_at and placement_closes_at to camp_map_settings"
```

---

### Task 4: Controller — new action + pass dates to Index

**Files:**
- Modify: `src/Humans.Web/Controllers/BarrioMapController.cs`

- [ ] **Step 1: Add `UpdatePlacementDates` action**

In `BarrioMapController.cs`, add after `DeleteLimitZone`:

```csharp
[HttpPost("Admin/UpdatePlacementDates")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> UpdatePlacementDates(string? opensAt, string? closesAt, CancellationToken cancellationToken)
{
    var userId = CurrentUserId();
    if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
        return Forbid();

    var pattern = NodaTime.Text.LocalDateTimePattern.CreateWithInvariantCulture("yyyy-MM-ddTHH:mm");
    LocalDateTime? opens = opensAt is { Length: > 0 } ? pattern.Parse(opensAt).Value : null;
    LocalDateTime? closes = closesAt is { Length: > 0 } ? pattern.Parse(closesAt).Value : null;

    await _campMapService.UpdatePlacementDatesAsync(opens, closes, cancellationToken);
    return RedirectToAction(nameof(Admin));
}
```

- [ ] **Step 2: Add `using NodaTime;` to controller if not already present**

Check the top of `BarrioMapController.cs`. If `using NodaTime;` is missing, add it.

- [ ] **Step 3: Pass dates in `Index` action**

In the `Index` action, after the existing `ViewBag` assignments, add:

```csharp
ViewBag.PlacementOpensAt = settings.PlacementOpensAt;
ViewBag.PlacementClosesAt = settings.PlacementClosesAt;
```

- [ ] **Step 4: Build to verify**

```bash
dotnet build Humans.slnx
```

Expected: no errors.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/BarrioMapController.cs
git commit -m "feat: add UpdatePlacementDates controller action and pass dates to Index"
```

---

### Task 5: Admin view — placement dates form card

**Files:**
- Modify: `src/Humans.Web/Views/BarrioMap/Admin.cshtml`

- [ ] **Step 1: Add the dates card**

In `Admin.cshtml`, add a new card after the closing `</div>` of the Placement Phase card (inside `<div class="row g-4">`):

```razor
<!-- Placement Dates -->
<div class="col-md-6">
    <div class="card">
        <div class="card-header fw-semibold">
            <i class="fa fa-calendar me-1"></i> Placement Dates (@settings.Year)
        </div>
        <div class="card-body">
            <p class="text-muted small mb-3">Informational only — shown in the help modal. Does not automatically open or close placement.</p>
            <form method="post" action="/BarrioMap/Admin/UpdatePlacementDates">
                @Html.AntiForgeryToken()
                <div class="mb-3">
                    <label class="form-label small fw-semibold">Opens at</label>
                    <input type="datetime-local" name="opensAt" class="form-control form-control-sm"
                           value="@(settings.PlacementOpensAt?.ToString("yyyy-MM-ddTHH:mm", null) ?? string.Empty)" />
                </div>
                <div class="mb-3">
                    <label class="form-label small fw-semibold">Closes at</label>
                    <input type="datetime-local" name="closesAt" class="form-control form-control-sm"
                           value="@(settings.PlacementClosesAt?.ToString("yyyy-MM-ddTHH:mm", null) ?? string.Empty)" />
                </div>
                <button type="submit" class="btn btn-primary btn-sm">
                    <i class="fa fa-save me-1"></i> Save dates
                </button>
            </form>
        </div>
    </div>
</div>
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/BarrioMap/Admin.cshtml
git commit -m "feat: add placement dates form to barrio map admin panel"
```

---

### Task 6: Help modal — display dates

**Files:**
- Modify: `src/Humans.Web/Views/BarrioMap/_PlacementHelpModal.cshtml`

- [ ] **Step 1: Replace the "When is it?" section**

Replace the existing static paragraph:

```razor
<h6>When is it?</h6>
<p class="text-muted small">The placement phase runs for a limited time — exact dates will be shown here soon. Place your barrio before it closes!</p>
```

With:

```razor
<h6>When is it?</h6>
@{
    var opensAt = ViewBag.PlacementOpensAt as NodaTime.LocalDateTime?;
    var closesAt = ViewBag.PlacementClosesAt as NodaTime.LocalDateTime?;
    const string fmt = "d MMMM 'at' HH:mm";
}
@if (opensAt.HasValue || closesAt.HasValue)
{
    <p class="text-muted small">
        @if (opensAt.HasValue && closesAt.HasValue)
        {
            <span>Opens: <strong>@opensAt.Value.ToString(fmt, null)</strong> &middot; Closes: <strong>@closesAt.Value.ToString(fmt, null)</strong> (Spain time)</span>
        }
        else if (opensAt.HasValue)
        {
            <span>Opens: <strong>@opensAt.Value.ToString(fmt, null)</strong> (Spain time)</span>
        }
        else
        {
            <span>Closes: <strong>@closesAt!.Value.ToString(fmt, null)</strong> (Spain time)</span>
        }
    </p>
}
else
{
    <p class="text-muted small">The placement phase runs for a limited time — exact dates will be shown here soon. Place your barrio before it closes!</p>
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build Humans.slnx
```

Expected: no errors.

- [ ] **Step 3: Run all tests**

```bash
dotnet test Humans.slnx
```

Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/BarrioMap/_PlacementHelpModal.cshtml
git commit -m "feat: display informational placement dates in help modal"
```
