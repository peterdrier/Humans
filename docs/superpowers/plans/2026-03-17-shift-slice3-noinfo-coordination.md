# Shift Slice 3 — NoInfo & Coordination Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build operational coordination features: NoInfo dashboard, voluntell, no-show marking UI, build/strike staffing chart, volunteer event profile badges, and shifts summary card on department pages.

**Architecture:** Server-rendered ASP.NET MVC with Bootstrap 5 and Chart.js for visualization. New `ShiftDashboardController` for the dashboard route, extensions to existing `ShiftAdminController`, `TeamController`, and `HumanController`. Service layer additions to `ShiftManagementService` and `ShiftSignupService`. No database migrations — all data already exists from Slices 1 & 2.

**Tech Stack:** ASP.NET Core 9, EF Core, Razor views, Bootstrap 5, Chart.js 4, NodaTime

**Spec:** `docs/superpowers/specs/2026-03-17-shift-slice3-noinfo-coordination-design.md`

---

## File Structure

### Files to Create

| File | Responsibility |
|------|---------------|
| `Controllers/ShiftDashboardController.cs` | Dashboard index + voluntell search + voluntell assign |
| `Views/ShiftDashboard/Index.cshtml` | Dashboard view: filters, staffing chart, urgency table, voluntell panels |
| `Views/Shared/_StaffingChart.cshtml` | Reusable Chart.js stacked bar chart partial |
| `Views/Shared/_VolunteerProfileBadges.cshtml` | Reusable volunteer profile badge rendering partial |
| `Views/Shared/_ShiftsSummaryCard.cshtml` | Team page shifts summary card partial |

### Files to Modify

| File | Changes |
|------|---------|
| `Models/ShiftViewModels.cs` | Add `ShiftDashboardViewModel`, `ShiftsSummaryCardViewModel`, `VolunteerSearchResult`, `NoShowHistoryItem`; extend `ShiftAdminViewModel` |
| `Interfaces/IShiftManagementService.cs` | Add `GetStaffingDataAsync`, `GetShiftsSummaryAsync`, `GetDepartmentsWithRotasAsync`; add `DailyStaffingData` and `ShiftsSummaryData` records |
| `Interfaces/IShiftSignupService.cs` | Add `GetNoShowHistoryAsync` |
| `Services/ShiftManagementService.cs` | Implement new service methods; update `GetRotasByDepartmentAsync` to include signup users |
| `Services/ShiftSignupService.cs` | Implement `GetNoShowHistoryAsync` |
| `Controllers/ShiftAdminController.cs` | Add `SearchVolunteers` + `Voluntell` actions; pass profile data, `CanViewMedical`, and `StaffingData` to view |
| `Views/ShiftAdmin/Index.cshtml` | Past-shift signup expansion with no-show buttons + profile badges + voluntell inline panel + staffing chart |
| `Controllers/TeamController.cs` | Load shifts summary data for departments |
| `Models/TeamViewModels.cs` | Add `ShiftsSummary` property to `TeamDetailViewModel` |
| `Views/Team/Details.cshtml` | Render shifts summary card partial |
| `Controllers/HumanController.cs` | Load no-show history for profile view when viewer is coordinator/admin |
| `Views/Profile/Index.cshtml` | Render no-show history section (Note: `HumanController.View` renders `~/Views/Profile/Index.cshtml` explicitly — the spec's reference to `Views/Human/View.cshtml` is incorrect) |

---

## Chunk 1: Service Layer & View Models

### Task 1: Add DTOs to interface files and view models

**Files:**
- Modify: `src/Humans.Application/Interfaces/IShiftManagementService.cs`
- Modify: `src/Humans.Application/Interfaces/IShiftSignupService.cs`
- Modify: `src/Humans.Web/Models/ShiftViewModels.cs`

- [ ] **Step 1: Add records and methods to IShiftManagementService.cs**

After the existing `UrgentShift` record at the end of the file, add:

```csharp
/// <summary>
/// Per-day staffing data for build/strike visualization.
/// </summary>
public record DailyStaffingData(
    int DayOffset,
    string DateLabel,
    int ConfirmedCount,
    int TotalSlots,
    string Period);

/// <summary>
/// Aggregated shift summary for a department.
/// </summary>
public record ShiftsSummaryData(
    int TotalSlots,
    int ConfirmedCount,
    int PendingCount,
    int UniqueVolunteerCount);
```

Inside the `IShiftManagementService` interface, add at the end before the closing brace:

```csharp
// === Staffing & Summary ===

/// <summary>
/// Gets per-day staffing data for build/strike periods.
/// </summary>
Task<IReadOnlyList<DailyStaffingData>> GetStaffingDataAsync(
    Guid eventSettingsId, Guid? departmentId = null);

/// <summary>
/// Gets shifts summary for a department. Returns null if no rotas.
/// </summary>
Task<ShiftsSummaryData?> GetShiftsSummaryAsync(
    Guid eventSettingsId, Guid departmentTeamId);

/// <summary>
/// Gets all parent teams that have active rotas in the given event.
/// </summary>
Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
    Guid eventSettingsId);
```

- [ ] **Step 2: Add GetNoShowHistoryAsync to IShiftSignupService.cs**

Add at the end of the interface:

```csharp
/// <summary>
/// Gets all no-show signups for a user, with shift/team context and reviewer info.
/// </summary>
Task<IReadOnlyList<ShiftSignup>> GetNoShowHistoryAsync(Guid userId);
```

- [ ] **Step 3: Add view models to ShiftViewModels.cs**

Add after the existing `NotificationSettingsViewModel` at the end:

```csharp
// === Dashboard ===

public class ShiftDashboardViewModel
{
    public List<UrgentShift> Shifts { get; set; } = [];
    public List<DepartmentOption> Departments { get; set; } = [];
    public Guid? SelectedDepartmentId { get; set; }
    public string? SelectedDate { get; set; }
    public EventSettings EventSettings { get; set; } = null!;
    public List<DailyStaffingData> StaffingData { get; set; } = [];
}

public class VolunteerSearchResult
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Skills { get; set; } = [];
    public List<string> Quirks { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public string? DietaryPreference { get; set; }
    public int BookedShiftCount { get; set; }
    public bool HasOverlap { get; set; }
}

// === Shifts Summary Card ===

public class ShiftsSummaryCardViewModel
{
    public int TotalSlots { get; set; }
    public int ConfirmedCount { get; set; }
    public int PendingCount { get; set; }
    public int UniqueVolunteerCount { get; set; }
    public string ShiftsUrl { get; set; } = "";
    public bool CanManageShifts { get; set; }
}

// === No-Show History ===

public class NoShowHistoryItem
{
    public string ShiftTitle { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string ShiftDateLabel { get; set; } = string.Empty;
    public string? MarkedByName { get; set; }
    public string? MarkedAtLabel { get; set; }
}
```

Add `using Humans.Application.Interfaces;` to the top of `ShiftViewModels.cs` (for `UrgentShift` and `DailyStaffingData` references).

- [ ] **Step 4: Extend ShiftAdminViewModel**

Add three properties to the existing `ShiftAdminViewModel` class, after `CanApproveSignups`:

```csharp
public Dictionary<Guid, VolunteerEventProfile> VolunteerProfiles { get; set; } = new();
public bool CanViewMedical { get; set; }
public List<DailyStaffingData> StaffingData { get; set; } = [];
```

- [ ] **Step 5: Build (expected to fail — unimplemented interfaces)**

Run: `dotnet build Humans.slnx`
Expected: Build FAILS with unimplemented interface methods in `ShiftManagementService` and `ShiftSignupService`

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/IShiftManagementService.cs src/Humans.Application/Interfaces/IShiftSignupService.cs src/Humans.Web/Models/ShiftViewModels.cs
git commit -m "feat(slice3): add DTOs, interfaces, and view models for dashboard, staffing, summary, and no-show"
```

---

### Task 2: Implement service methods

**Files:**
- Modify: `src/Humans.Infrastructure/Services/ShiftManagementService.cs`
- Modify: `src/Humans.Infrastructure/Services/ShiftSignupService.cs`

- [ ] **Step 1: Implement all new methods in ShiftManagementService**

Add at the end of the class, before the closing brace:

```csharp
// ============================================================
// Staffing & Summary
// ============================================================

public async Task<IReadOnlyList<DailyStaffingData>> GetStaffingDataAsync(
    Guid eventSettingsId, Guid? departmentId = null)
{
    var es = await _dbContext.EventSettings.AsNoTracking()
        .FirstOrDefaultAsync(e => e.Id == eventSettingsId);
    if (es == null) return [];

    var tz = DateTimeZoneProviders.Tzdb[es.TimeZoneId];

    // Build period: [BuildStartOffset..-1] and Strike period: [EventEndOffset+1..StrikeEndOffset]
    var dayOffsets = new List<int>();
    for (var d = es.BuildStartOffset; d < 0; d++) dayOffsets.Add(d);
    for (var d = es.EventEndOffset + 1; d <= es.StrikeEndOffset; d++) dayOffsets.Add(d);

    if (dayOffsets.Count == 0) return [];

    // Load all active shifts in build/strike range
    var query = _dbContext.Shifts
        .AsNoTracking()
        .Include(s => s.Rota).ThenInclude(r => r.EventSettings)
        .Include(s => s.ShiftSignups)
        .Where(s => s.Rota.EventSettingsId == eventSettingsId && s.IsActive && s.Rota.IsActive);

    if (departmentId.HasValue)
        query = query.Where(s => s.Rota.TeamId == departmentId.Value);

    var shifts = await query.ToListAsync();
    var results = new List<DailyStaffingData>();

    foreach (var dayOffset in dayOffsets)
    {
        var dayDate = es.GateOpeningDate.PlusDays(dayOffset);
        var dayStart = dayDate.AtStartOfDayInZone(tz).ToInstant();
        var dayEnd = dayDate.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();
        var period = dayOffset < 0 ? "Build" : "Strike";
        var dateLabel = dayDate.DayOfWeek.ToString()[..3] + " " + dayDate.ToString("MMM d", null);

        var overlapping = shifts.Where(s =>
        {
            var start = s.GetAbsoluteStart(es);
            var end = s.GetAbsoluteEnd(es);
            return start < dayEnd && end > dayStart;
        }).ToList();

        var totalSlots = overlapping.Sum(s => s.MaxVolunteers);
        var confirmedCount = overlapping
            .SelectMany(s => s.ShiftSignups)
            .Where(su => su.Status == SignupStatus.Confirmed)
            .Select(su => su.UserId)
            .Distinct()
            .Count();

        results.Add(new DailyStaffingData(dayOffset, dateLabel, confirmedCount, totalSlots, period));
    }

    return results;
}

public async Task<ShiftsSummaryData?> GetShiftsSummaryAsync(
    Guid eventSettingsId, Guid departmentTeamId)
{
    var rotas = await _dbContext.Rotas
        .AsNoTracking()
        .Include(r => r.Shifts).ThenInclude(s => s.ShiftSignups)
        .Where(r => r.EventSettingsId == eventSettingsId && r.TeamId == departmentTeamId)
        .ToListAsync();

    if (rotas.Count == 0) return null;

    var activeShifts = rotas.SelectMany(r => r.Shifts).Where(s => s.IsActive).ToList();
    if (activeShifts.Count == 0) return null;

    var allSignups = activeShifts.SelectMany(s => s.ShiftSignups).ToList();

    return new ShiftsSummaryData(
        TotalSlots: activeShifts.Sum(s => s.MaxVolunteers),
        ConfirmedCount: allSignups.Count(s => s.Status == SignupStatus.Confirmed),
        PendingCount: allSignups.Count(s => s.Status == SignupStatus.Pending),
        UniqueVolunteerCount: allSignups
            .Where(s => s.Status == SignupStatus.Confirmed)
            .Select(s => s.UserId)
            .Distinct()
            .Count());
}

public async Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(
    Guid eventSettingsId)
{
    var teams = await _dbContext.Rotas
        .AsNoTracking()
        .Where(r => r.EventSettingsId == eventSettingsId && r.IsActive)
        .Select(r => new { r.Team.Id, r.Team.Name })
        .Distinct()
        .OrderBy(x => x.Name)
        .ToListAsync();

    return teams.Select(x => (x.Id, x.Name)).ToList();
}
```

- [ ] **Step 2: Update GetRotasByDepartmentAsync to include signup users**

Change:
```csharp
.Include(r => r.Shifts)
    .ThenInclude(s => s.ShiftSignups)
```
To:
```csharp
.Include(r => r.Shifts)
    .ThenInclude(s => s.ShiftSignups)
        .ThenInclude(su => su.User)
```

- [ ] **Step 3: Implement GetNoShowHistoryAsync in ShiftSignupService**

Add before the private helper methods:

```csharp
public async Task<IReadOnlyList<ShiftSignup>> GetNoShowHistoryAsync(Guid userId)
{
    return await _dbContext.ShiftSignups
        .AsNoTracking()
        .Include(s => s.Shift).ThenInclude(sh => sh.Rota).ThenInclude(r => r.Team)
        .Include(s => s.Shift).ThenInclude(sh => sh.Rota).ThenInclude(r => r.EventSettings)
        .Include(s => s.ReviewedByUser)
        .Where(s => s.UserId == userId && s.Status == SignupStatus.NoShow)
        .OrderByDescending(s => s.ReviewedAt)
        .ToListAsync();
}
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build Humans.slnx`
Expected: Build success

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/ShiftManagementService.cs src/Humans.Infrastructure/Services/ShiftSignupService.cs
git commit -m "feat(slice3): implement staffing data, shifts summary, departments, and no-show history"
```

---

## Chunk 2: Shared Partials

### Task 3: Create _VolunteerProfileBadges partial

**Files:**
- Create: `src/Humans.Web/Views/Shared/_VolunteerProfileBadges.cshtml`

- [ ] **Step 1: Create the partial view**

```cshtml
@using Humans.Domain.Entities
@model (VolunteerEventProfile? Profile, bool ShowMedical)

@if (Model.Profile != null)
{
    <span class="volunteer-badges">
        @foreach (var skill in Model.Profile.Skills)
        {
            <span class="badge bg-info me-1">@skill</span>
        }
        @foreach (var quirk in Model.Profile.Quirks)
        {
            <span class="badge bg-secondary me-1">@quirk</span>
        }
        @if (!string.IsNullOrEmpty(Model.Profile.DietaryPreference))
        {
            <span class="badge bg-success me-1">@Model.Profile.DietaryPreference</span>
        }
        @foreach (var lang in Model.Profile.Languages)
        {
            <span class="badge bg-primary me-1">@lang</span>
        }
        @if (Model.ShowMedical && !string.IsNullOrEmpty(Model.Profile.MedicalConditions))
        {
            <span class="badge bg-danger" title="@Model.Profile.MedicalConditions">Medical</span>
        }
    </span>
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Shared/_VolunteerProfileBadges.cshtml
git commit -m "feat(slice3): add volunteer profile badges partial view"
```

---

### Task 4: Create _StaffingChart partial

**Files:**
- Create: `src/Humans.Web/Views/Shared/_StaffingChart.cshtml`

Note: Chart.js is NOT in the global layout — it's loaded per-page (see `Ticket/Index.cshtml`). The partial must include the script tag. To avoid double-loading if two charts appear on one page, use a simple guard.

- [ ] **Step 1: Create the partial view**

```cshtml
@using Humans.Application.Interfaces
@model (List<DailyStaffingData> Data, string ChartId)

@if (Model.Data.Count > 0)
{
    <div class="card mb-4">
        <div class="card-header"><h6 class="mb-0">Build / Strike Staffing</h6></div>
        <div class="card-body">
            <canvas id="@Model.ChartId" height="200"></canvas>
        </div>
    </div>

    <script>
    if (typeof Chart === 'undefined') {
        var s = document.createElement('script');
        s.src = 'https://cdn.jsdelivr.net/npm/chart.js@4/dist/chart.umd.min.js';
        s.onload = function() { window['render_@(Model.ChartId)'](); };
        document.head.appendChild(s);
    } else {
        document.addEventListener('DOMContentLoaded', function() { window['render_@(Model.ChartId)'](); });
    }

    window['render_@(Model.ChartId)'] = function() {
        var data = @Html.Raw(System.Text.Json.JsonSerializer.Serialize(Model.Data));
        var labels = data.map(function(d) { return d.dateLabel; });
        var confirmed = data.map(function(d) { return d.confirmedCount; });
        var remaining = data.map(function(d) { return Math.max(0, d.totalSlots - d.confirmedCount); });

        var confirmedColors = data.map(function(d) {
            if (d.totalSlots === 0) return '#198754';
            var pct = d.confirmedCount / d.totalSlots;
            if (pct >= 0.8) return '#198754';
            if (pct >= 0.5) return '#ffc107';
            return '#dc3545';
        });

        new Chart(document.getElementById('@Model.ChartId'), {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [
                    { label: 'Confirmed', data: confirmed, backgroundColor: confirmedColors },
                    { label: 'Remaining', data: remaining, backgroundColor: '#e9ecef' }
                ]
            },
            options: {
                responsive: true,
                scales: { x: { stacked: true }, y: { stacked: true, beginAtZero: true } },
                plugins: {
                    tooltip: {
                        callbacks: {
                            afterTitle: function(items) { return data[items[0].dataIndex].period; }
                        }
                    }
                }
            }
        });
    };
    </script>
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Shared/_StaffingChart.cshtml
git commit -m "feat(slice3): add staffing chart partial view with Chart.js"
```

---

### Task 5: Create _ShiftsSummaryCard partial

**Files:**
- Create: `src/Humans.Web/Views/Shared/_ShiftsSummaryCard.cshtml`

- [ ] **Step 1: Create the partial view**

```cshtml
@model Humans.Web.Models.ShiftsSummaryCardViewModel

<div class="card mb-4">
    <div class="card-header">
        <h6 class="mb-0"><i class="fa-solid fa-clock me-1"></i> Shifts</h6>
    </div>
    <div class="card-body">
        @{
            var fillPct = Model.TotalSlots > 0 ? (int)(100.0 * Model.ConfirmedCount / Model.TotalSlots) : 0;
            var barClass = fillPct >= 80 ? "bg-success" : fillPct >= 50 ? "bg-warning" : "bg-danger";
        }
        <div class="progress mb-3" style="height: 20px;">
            <div class="progress-bar @barClass" role="progressbar"
                 style="width: @fillPct%;" aria-valuenow="@fillPct" aria-valuemin="0" aria-valuemax="100">
                @fillPct%
            </div>
        </div>
        <div class="d-flex justify-content-between align-items-center mb-2">
            <span>@Model.ConfirmedCount / @Model.TotalSlots slots filled</span>
            <span>@Model.UniqueVolunteerCount humans signed up</span>
        </div>
        @if (Model.PendingCount > 0)
        {
            <a href="@Model.ShiftsUrl" class="badge bg-warning text-dark text-decoration-none">
                @Model.PendingCount pending
            </a>
        }
        @if (Model.CanManageShifts)
        {
            <a href="@Model.ShiftsUrl" class="btn btn-sm btn-outline-primary mt-2 w-100">Manage Shifts</a>
        }
    </div>
</div>
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Shared/_ShiftsSummaryCard.cshtml
git commit -m "feat(slice3): add shifts summary card partial view"
```

---

## Chunk 3: Dashboard Controller & View

### Task 6: Create ShiftDashboardController

**Files:**
- Create: `src/Humans.Web/Controllers/ShiftDashboardController.cs`

- [ ] **Step 1: Create the controller**

```csharp
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Shifts/Dashboard")]
public class ShiftDashboardController : Controller
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _signupService;
    private readonly IProfileService _profileService;
    private readonly UserManager<User> _userManager;

    public ShiftDashboardController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        IProfileService profileService,
        UserManager<User> userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _profileService = profileService;
        _userManager = userManager;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? departmentId, string? date)
    {
        if (!User.IsInRole(RoleNames.NoInfoAdmin) && !User.IsInRole(RoleNames.Admin))
            return Forbid();

        var es = await _shiftMgmt.GetActiveAsync();
        if (es == null)
        {
            TempData["ErrorMessage"] = "No active event settings configured.";
            return RedirectToAction("Index", "Home");
        }

        LocalDate? filterDate = null;
        if (!string.IsNullOrEmpty(date))
        {
            var parseResult = LocalDatePattern.Iso.Parse(date);
            if (parseResult.Success)
                filterDate = parseResult.Value;
        }

        var shifts = await _shiftMgmt.GetUrgentShiftsAsync(es.Id, limit: null, departmentId, filterDate);
        var staffingData = await _shiftMgmt.GetStaffingDataAsync(es.Id);

        var deptTuples = await _shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);
        var departments = deptTuples.Select(d => new DepartmentOption
        {
            TeamId = d.TeamId,
            Name = d.TeamName
        }).ToList();

        var model = new ShiftDashboardViewModel
        {
            Shifts = shifts.ToList(),
            Departments = departments,
            SelectedDepartmentId = departmentId,
            SelectedDate = date,
            EventSettings = es,
            StaffingData = staffingData.ToList()
        };

        return View(model);
    }

    [HttpGet("SearchVolunteers")]
    public async Task<IActionResult> SearchVolunteers(Guid shiftId, string? query)
    {
        if (!User.IsInRole(RoleNames.NoInfoAdmin) && !User.IsInRole(RoleNames.Admin))
            return Forbid();

        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Json(Array.Empty<VolunteerSearchResult>());

        var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
        if (shift == null) return NotFound();

        var es = shift.Rota.EventSettings ?? await _shiftMgmt.GetActiveAsync();
        if (es == null) return NotFound();

        var results = await BuildVolunteerSearchResultsAsync(shift, query, es);
        return Json(results);
    }

    [HttpPost("Voluntell")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Voluntell(Guid shiftId, Guid userId)
    {
        if (!User.IsInRole(RoleNames.NoInfoAdmin) && !User.IsInRole(RoleNames.Admin))
            return Forbid();

        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Unauthorized();

        var result = await _signupService.VoluntellAsync(userId, shiftId, currentUser.Id);
        TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] =
            result.Success ? "Volunteer assigned to shift." : result.Error;

        return RedirectToAction(nameof(Index));
    }

    internal async Task<List<VolunteerSearchResult>> BuildVolunteerSearchResultsAsync(
        Shift shift, string query, EventSettings es)
    {
        var shiftStart = shift.GetAbsoluteStart(es);
        var shiftEnd = shift.GetAbsoluteEnd(es);

        var users = await _userManager.Users
            .Where(u => u.DisplayName.Contains(query))
            .Take(10)
            .ToListAsync();

        var results = new List<VolunteerSearchResult>();
        foreach (var user in users)
        {
            var profile = await _profileService.GetShiftProfileAsync(user.Id, includeMedical: false);
            var userSignups = await _signupService.GetByUserAsync(user.Id, es.Id);
            var confirmedSignups = userSignups.Where(s => s.Status == SignupStatus.Confirmed).ToList();

            var hasOverlap = confirmedSignups.Any(s =>
            {
                var sStart = s.Shift.GetAbsoluteStart(es);
                var sEnd = s.Shift.GetAbsoluteEnd(es);
                return shiftStart < sEnd && shiftEnd > sStart;
            });

            results.Add(new VolunteerSearchResult
            {
                UserId = user.Id,
                DisplayName = user.DisplayName,
                Skills = profile?.Skills ?? [],
                Quirks = profile?.Quirks ?? [],
                Languages = profile?.Languages ?? [],
                DietaryPreference = profile?.DietaryPreference,
                BookedShiftCount = confirmedSignups.Count,
                HasOverlap = hasOverlap
            });
        }

        return results;
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build success

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftDashboardController.cs
git commit -m "feat(slice3): add ShiftDashboardController with urgency view, voluntell search, and assign"
```

---

### Task 7: Create Dashboard view

**Files:**
- Create: `src/Humans.Web/Views/ShiftDashboard/Index.cshtml`

- [ ] **Step 1: Create the view**

```cshtml
@using Humans.Application.Interfaces
@using Humans.Domain.Enums
@using Humans.Web.Controllers
@model Humans.Web.Models.ShiftDashboardViewModel

@{
    ViewData["Title"] = "Shifts Dashboard";
    var antiForgeryHtml = Html.AntiForgeryToken().ToString();
}

<div class="container py-4">
    <h2>Shifts Dashboard</h2>

    <vc:temp-data-alerts />

    @* Filter bar *@
    <form method="get" class="card mb-4">
        <div class="card-body">
            <div class="row g-2 align-items-end">
                <div class="col-md-3">
                    <label class="form-label">Date</label>
                    <input type="date" name="date" value="@Model.SelectedDate" class="form-control" />
                </div>
                <div class="col-md-3">
                    <label class="form-label">Department</label>
                    <select name="departmentId" class="form-select">
                        <option value="">All Departments</option>
                        @foreach (var dept in Model.Departments)
                        {
                            <option value="@dept.TeamId" selected="@(dept.TeamId == Model.SelectedDepartmentId)">@dept.Name</option>
                        }
                    </select>
                </div>
                <div class="col-md-2">
                    <button type="submit" class="btn btn-primary">Filter</button>
                    @if (Model.SelectedDepartmentId.HasValue || !string.IsNullOrEmpty(Model.SelectedDate))
                    {
                        <a asp-action="Index" class="btn btn-outline-secondary ms-1">Clear</a>
                    }
                </div>
            </div>
        </div>
    </form>

    @* Staffing chart — always global, ignores department filter *@
    @await Html.PartialAsync("_StaffingChart", (Model.StaffingData, "dashboardStaffingChart"))

    @* Urgency table *@
    <div class="card">
        <div class="card-header">
            <h5 class="mb-0">Unfilled Shifts (@Model.Shifts.Count)</h5>
        </div>
        <div class="card-body p-0">
            @if (Model.Shifts.Count == 0)
            {
                <p class="text-muted p-3 mb-0">No unfilled shifts found.</p>
            }
            else
            {
                <div class="table-responsive">
                    <table class="table table-sm table-hover mb-0">
                        <thead>
                            <tr>
                                <th>Shift</th>
                                <th>Department</th>
                                <th>Date / Time</th>
                                <th>Slots</th>
                                <th>Priority</th>
                                <th></th>
                            </tr>
                        </thead>
                        <tbody>
                            @foreach (var item in Model.Shifts)
                            {
                                var shift = item.Shift;
                                var es = Model.EventSettings;
                                var shiftDate = es.GateOpeningDate.PlusDays(shift.DayOffset);
                                var dateLabel = shiftDate.DayOfWeek.ToString()[..3] + " " + shiftDate.ToString("MMM d", null);
                                var period = shift.GetShiftPeriod(es);
                                var periodBadge = period switch
                                {
                                    ShiftPeriod.Build => "bg-info",
                                    ShiftPeriod.Event => "bg-success",
                                    ShiftPeriod.Strike => "bg-secondary",
                                    _ => "bg-secondary"
                                };
                                var periodLabel = period switch
                                {
                                    ShiftPeriod.Build => "Build",
                                    ShiftPeriod.Event => "Burn",
                                    ShiftPeriod.Strike => "Strike",
                                    _ => ""
                                };
                                var priorityBadge = shift.Rota.Priority switch
                                {
                                    ShiftPriority.Essential => "bg-danger",
                                    ShiftPriority.Important => "bg-warning text-dark",
                                    _ => "bg-secondary"
                                };
                                var understaffed = item.ConfirmedCount < shift.MinVolunteers;
                                var collapseId = "voluntell-" + shift.Id.ToString("N");
                                <tr>
                                    <td>@shift.Title</td>
                                    <td>@item.DepartmentName</td>
                                    <td>
                                        <span class="badge @periodBadge me-1">@periodLabel</span>
                                        @dateLabel @shift.StartTime
                                    </td>
                                    <td>
                                        <span class="@(understaffed ? "text-danger fw-bold" : "")">@item.ConfirmedCount</span>
                                        <small class="text-muted">/ @shift.MaxVolunteers</small>
                                    </td>
                                    <td><span class="badge @priorityBadge">@shift.Rota.Priority</span></td>
                                    <td>
                                        <button class="btn btn-sm btn-outline-primary" type="button"
                                                data-bs-toggle="collapse" data-bs-target="#@collapseId">
                                            Voluntell
                                        </button>
                                    </td>
                                </tr>
                                <tr class="collapse" id="@collapseId">
                                    <td colspan="6">
                                        <div class="p-3 bg-light">
                                            <div class="input-group mb-2" style="max-width: 400px;">
                                                <input type="text" class="form-control voluntell-search"
                                                       data-shift-id="@shift.Id"
                                                       placeholder="Search by name (min 2 chars)..." />
                                            </div>
                                            <div class="voluntell-results" data-shift-id="@shift.Id"></div>
                                        </div>
                                    </td>
                                </tr>
                            }
                        </tbody>
                    </table>
                </div>
            }
        </div>
    </div>
</div>

<script>
(function() {
    var debounceTimers = {};
    var searchUrl = '@Url.Action(nameof(ShiftDashboardController.SearchVolunteers))';
    var voluntellUrl = '@Url.Action(nameof(ShiftDashboardController.Voluntell))';
    var tokenHtml = '@Html.Raw(antiForgeryHtml?.Replace("'", "\\'"))';

    document.querySelectorAll('.voluntell-search').forEach(function(input) {
        input.addEventListener('input', function() {
            var shiftId = this.dataset.shiftId;
            var query = this.value.trim();
            var resultsDiv = document.querySelector('.voluntell-results[data-shift-id="' + shiftId + '"]');

            clearTimeout(debounceTimers[shiftId]);
            if (query.length < 2) { resultsDiv.innerHTML = ''; return; }

            debounceTimers[shiftId] = setTimeout(function() {
                fetch(searchUrl + '?shiftId=' + encodeURIComponent(shiftId) + '&query=' + encodeURIComponent(query))
                    .then(function(r) { return r.json(); })
                    .then(function(data) {
                        if (data.length === 0) { resultsDiv.innerHTML = '<p class="text-muted mb-0">No results found.</p>'; return; }
                        var html = '';
                        data.forEach(function(v) {
                            html += '<div class="d-flex justify-content-between align-items-center border-bottom py-2">';
                            html += '<div><strong>' + escapeHtml(v.displayName) + '</strong>';
                            html += ' <small class="text-muted">(' + v.bookedShiftCount + ' shifts booked)</small>';
                            if (v.hasOverlap) html += ' <span class="badge bg-warning text-dark">Overlap</span>';
                            html += '<div class="mt-1">';
                            (v.skills || []).forEach(function(s) { html += '<span class="badge bg-info me-1">' + escapeHtml(s) + '</span>'; });
                            (v.quirks || []).forEach(function(s) { html += '<span class="badge bg-secondary me-1">' + escapeHtml(s) + '</span>'; });
                            if (v.dietaryPreference) html += '<span class="badge bg-success me-1">' + escapeHtml(v.dietaryPreference) + '</span>';
                            (v.languages || []).forEach(function(s) { html += '<span class="badge bg-primary me-1">' + escapeHtml(s) + '</span>'; });
                            html += '</div></div>';
                            html += '<form method="post" action="' + voluntellUrl + '">';
                            html += tokenHtml;
                            html += '<input type="hidden" name="shiftId" value="' + shiftId + '" />';
                            html += '<input type="hidden" name="userId" value="' + v.userId + '" />';
                            html += '<button type="submit" class="btn btn-sm btn-primary"' + (v.hasOverlap ? ' title="Warning: overlap detected"' : '') + '>Assign</button>';
                            html += '</form></div>';
                        });
                        resultsDiv.innerHTML = html;
                    })
                    .catch(function() { resultsDiv.innerHTML = '<p class="text-danger">Search failed.</p>'; });
            }, 300);
        });
    });

    function escapeHtml(text) { var d = document.createElement('div'); d.textContent = text; return d.innerHTML; }
})();
</script>
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build success

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/ShiftDashboard/Index.cshtml
git commit -m "feat(slice3): add dashboard view with urgency table, filters, voluntell, and staffing chart"
```

---

## Chunk 4: Department Shifts Page Enhancements

### Task 8: Add voluntell and profile loading to ShiftAdminController

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftAdminController.cs`

- [ ] **Step 1: Add IProfileService dependency**

Add field:
```csharp
private readonly IProfileService _profileService;
```

Update constructor signature and body to accept and assign `IProfileService profileService`.

- [ ] **Step 2: Update Index action to load profiles and staffing data**

After the `foreach (var rota in rotas)` loop (which computes `totalSlots`, `confirmedCount`, `pendingSignups`) and before creating the model, add:

```csharp
// Batch-load volunteer event profiles for signup display
var allUserIds = rotas.SelectMany(r => r.Shifts)
    .SelectMany(s => s.ShiftSignups)
    .Select(su => su.UserId)
    .Distinct()
    .ToList();

var canViewMedical = User.IsInRole(RoleNames.NoInfoAdmin) || User.IsInRole(RoleNames.Admin);
var profileDict = new Dictionary<Guid, VolunteerEventProfile>();
foreach (var uid in allUserIds)
{
    var profile = await _profileService.GetShiftProfileAsync(uid, includeMedical: canViewMedical);
    if (profile != null)
        profileDict[uid] = profile;
}

var staffingData = await _shiftMgmt.GetStaffingDataAsync(es.Id, team.Id);
```

Then extend the model initialization with:
```csharp
VolunteerProfiles = profileDict,
CanViewMedical = canViewMedical,
StaffingData = staffingData.ToList()
```

- [ ] **Step 3: Add SearchVolunteers action**

Add after the `MarkNoShow` action:

```csharp
[HttpGet("SearchVolunteers")]
public async Task<IActionResult> SearchVolunteers(string slug, Guid shiftId, string? query)
{
    var (team, userId) = await ResolveTeamAndUserAsync(slug);
    if (team == null || userId == null) return NotFound();
    if (!await _shiftMgmt.CanApproveSignupsAsync(userId.Value, team.Id)) return Forbid();

    if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        return Json(Array.Empty<VolunteerSearchResult>());

    var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
    if (shift == null) return NotFound();
    if (shift.Rota.TeamId != team.Id) return NotFound();

    var es = shift.Rota.EventSettings ?? await _shiftMgmt.GetActiveAsync();
    if (es == null) return NotFound();

    var shiftStart = shift.GetAbsoluteStart(es);
    var shiftEnd = shift.GetAbsoluteEnd(es);

    var users = await _userManager.Users
        .Where(u => u.DisplayName.Contains(query))
        .Take(10)
        .ToListAsync();

    var results = new List<VolunteerSearchResult>();
    foreach (var user in users)
    {
        var profile = await _profileService.GetShiftProfileAsync(user.Id, includeMedical: false);
        var userSignups = await _signupService.GetByUserAsync(user.Id, es.Id);
        var confirmed = userSignups.Where(s => s.Status == SignupStatus.Confirmed).ToList();

        var hasOverlap = confirmed.Any(s =>
        {
            var sStart = s.Shift.GetAbsoluteStart(es);
            var sEnd = s.Shift.GetAbsoluteEnd(es);
            return shiftStart < sEnd && shiftEnd > sStart;
        });

        results.Add(new VolunteerSearchResult
        {
            UserId = user.Id,
            DisplayName = user.DisplayName,
            Skills = profile?.Skills ?? [],
            Quirks = profile?.Quirks ?? [],
            Languages = profile?.Languages ?? [],
            DietaryPreference = profile?.DietaryPreference,
            BookedShiftCount = confirmed.Count,
            HasOverlap = hasOverlap
        });
    }

    return Json(results);
}

[HttpPost("Voluntell")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Voluntell(string slug, Guid shiftId, Guid userId)
{
    var (team, currentUserId) = await ResolveTeamAndUserAsync(slug);
    if (team == null || currentUserId == null) return NotFound();
    if (!await _shiftMgmt.CanApproveSignupsAsync(currentUserId.Value, team.Id)) return Forbid();

    var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
    if (shift == null) return NotFound();
    if (shift.Rota.TeamId != team.Id) return NotFound();

    var result = await _signupService.VoluntellAsync(userId, shiftId, currentUserId.Value);
    TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] =
        result.Success ? "Volunteer assigned to shift." : result.Error;

    return RedirectToAction(nameof(Index), new { slug });
}
```

Add `using Microsoft.EntityFrameworkCore;` and `using Humans.Domain.Entities;` to the file if not already present.

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build success

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftAdminController.cs
git commit -m "feat(slice3): add voluntell search/assign and profile loading to dept shifts page"
```

---

### Task 9: Update ShiftAdmin/Index.cshtml

**Files:**
- Modify: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`

This task adds four features to the existing view: staffing chart, profile badges in pending approvals, past-shift signup expansion with no-show buttons, and voluntell inline panels.

- [ ] **Step 1: Add using directives and capture antiforgery token**

At the top of the file, after the `@using` lines, add:
```cshtml
@using Humans.Application.Interfaces
@using Humans.Domain.Entities
@using NodaTime
```

After the `ViewData["Title"]` block, add:
```cshtml
@{
    var antiForgeryHtml = Html.AntiForgeryToken().ToString();
}
```

- [ ] **Step 2: Add staffing chart after alerts**

After `<vc:temp-data-alerts />`, add:
```cshtml
@* Build/Strike Staffing Chart *@
@await Html.PartialAsync("_StaffingChart", (Model.StaffingData, "deptStaffingChart"))
```

- [ ] **Step 3: Add profile badges to pending approvals**

In the pending approvals thead, change:
```html
<tr><th>Human</th><th>Shift</th><th>Submitted</th><th></th></tr>
```
To:
```html
<tr><th>Human</th><th>Profile</th><th>Shift</th><th>Submitted</th><th></th></tr>
```

In the tbody loop, after `<td>@signup.User?.DisplayName</td>`, add:
```cshtml
<td>
    @{
        var pendingProfile = Model.VolunteerProfiles.GetValueOrDefault(signup.UserId);
    }
    @await Html.PartialAsync("_VolunteerProfileBadges", (pendingProfile, Model.CanViewMedical))
</td>
```

- [ ] **Step 4: Add past-shift signup expansion and voluntell panel**

In the shift table tbody, after the existing edit-shift collapse `<tr>` (the `edit-shift-@shift.Id.ToString("N")` row) and before the closing `}` of the foreach loop, add:

```cshtml
@{
    var shiftEnd = shift.GetAbsoluteEnd(Model.EventSettings);
    var now = SystemClock.Instance.GetCurrentInstant();
    var isPast = now > shiftEnd;
    var shiftSignups = shift.ShiftSignups
        .Where(su => su.Status == SignupStatus.Confirmed || su.Status == SignupStatus.NoShow || su.Status == SignupStatus.Bailed)
        .OrderBy(su => su.Status)
        .ThenBy(su => su.User?.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
@* Past-shift signup expansion with no-show buttons *@
@if (isPast && shiftSignups.Count > 0 && Model.CanApproveSignups)
{
    var signupsCollapseId = "signups-" + shift.Id.ToString("N");
    <tr>
        <td colspan="6">
            <button class="btn btn-sm btn-outline-info" type="button"
                    data-bs-toggle="collapse" data-bs-target="#@signupsCollapseId">
                Signups (@shiftSignups.Count)
            </button>
        </td>
    </tr>
    <tr class="collapse" id="@signupsCollapseId">
        <td colspan="6">
            <div class="p-2">
                @foreach (var signup in shiftSignups)
                {
                    <div class="d-flex justify-content-between align-items-center border-bottom py-1">
                        <div>
                            <span>@signup.User?.DisplayName</span>
                            @{
                                var signupProfile = Model.VolunteerProfiles.GetValueOrDefault(signup.UserId);
                            }
                            @await Html.PartialAsync("_VolunteerProfileBadges", (signupProfile, Model.CanViewMedical))
                            @if (signup.Status == SignupStatus.NoShow)
                            {
                                <span class="badge bg-danger ms-1">No-Show</span>
                            }
                            else if (signup.Status == SignupStatus.Bailed)
                            {
                                <span class="badge bg-secondary ms-1 text-muted">Bailed</span>
                            }
                        </div>
                        @if (signup.Status == SignupStatus.Confirmed)
                        {
                            <form asp-action="MarkNoShow" asp-route-slug="@Model.Department.Slug" asp-route-signupId="@signup.Id" method="post" class="d-inline">
                                @Html.AntiForgeryToken()
                                <button type="submit" class="btn btn-sm btn-outline-danger">Mark No-Show</button>
                            </form>
                        }
                    </div>
                }
            </div>
        </td>
    </tr>
}
@* Voluntell panel for active future shifts *@
@if (Model.CanApproveSignups && shift.IsActive && !isPast)
{
    var voluntellCollapseId = "dept-voluntell-" + shift.Id.ToString("N");
    <tr class="collapse" id="@voluntellCollapseId">
        <td colspan="6">
            <div class="p-3 bg-light">
                <div class="input-group mb-2" style="max-width: 400px;">
                    <input type="text" class="form-control dept-voluntell-search"
                           data-shift-id="@shift.Id"
                           placeholder="Search by name (min 2 chars)..." />
                </div>
                <div class="dept-voluntell-results" data-shift-id="@shift.Id"></div>
            </div>
        </td>
    </tr>
}
```

Also add a Voluntell button in the actions `<td>` (the last column that has Edit/Deactivate), after the Deactivate form:

```cshtml
@if (Model.CanApproveSignups && shift.IsActive && !isPast)
{
    var voluntellBtnId = "dept-voluntell-" + shift.Id.ToString("N");
    <button class="btn btn-sm btn-outline-success ms-1" type="button"
            data-bs-toggle="collapse" data-bs-target="#@voluntellBtnId">
        Voluntell
    </button>
}
```

Note: the `isPast` variable is computed above this point in the same foreach scope.

- [ ] **Step 5: Add voluntell JavaScript at the bottom**

Add at the end of the file:

```cshtml
<script>
(function() {
    var debounceTimers = {};
    var slug = '@Model.Department.Slug';
    var tokenHtml = '@Html.Raw(antiForgeryHtml?.Replace("'", "\\'"))';

    document.querySelectorAll('.dept-voluntell-search').forEach(function(input) {
        input.addEventListener('input', function() {
            var shiftId = this.dataset.shiftId;
            var query = this.value.trim();
            var resultsDiv = document.querySelector('.dept-voluntell-results[data-shift-id="' + shiftId + '"]');

            clearTimeout(debounceTimers[shiftId]);
            if (query.length < 2) { resultsDiv.innerHTML = ''; return; }

            debounceTimers[shiftId] = setTimeout(function() {
                fetch('/Teams/' + slug + '/Shifts/SearchVolunteers?shiftId=' + encodeURIComponent(shiftId) + '&query=' + encodeURIComponent(query))
                    .then(function(r) { return r.json(); })
                    .then(function(data) {
                        if (data.length === 0) { resultsDiv.innerHTML = '<p class="text-muted mb-0">No results.</p>'; return; }
                        var html = '';
                        data.forEach(function(v) {
                            html += '<div class="d-flex justify-content-between align-items-center border-bottom py-2">';
                            html += '<div><strong>' + escapeHtml(v.displayName) + '</strong>';
                            html += ' <small class="text-muted">(' + v.bookedShiftCount + ' shifts)</small>';
                            if (v.hasOverlap) html += ' <span class="badge bg-warning text-dark">Overlap</span>';
                            html += '<div class="mt-1">';
                            (v.skills || []).forEach(function(s) { html += '<span class="badge bg-info me-1">' + escapeHtml(s) + '</span>'; });
                            (v.quirks || []).forEach(function(s) { html += '<span class="badge bg-secondary me-1">' + escapeHtml(s) + '</span>'; });
                            if (v.dietaryPreference) html += '<span class="badge bg-success me-1">' + escapeHtml(v.dietaryPreference) + '</span>';
                            (v.languages || []).forEach(function(s) { html += '<span class="badge bg-primary me-1">' + escapeHtml(s) + '</span>'; });
                            html += '</div></div>';
                            html += '<form method="post" action="/Teams/' + slug + '/Shifts/Voluntell">';
                            html += tokenHtml;
                            html += '<input type="hidden" name="shiftId" value="' + shiftId + '" />';
                            html += '<input type="hidden" name="userId" value="' + v.userId + '" />';
                            html += '<button type="submit" class="btn btn-sm btn-primary">Assign</button>';
                            html += '</form></div>';
                        });
                        resultsDiv.innerHTML = html;
                    });
            }, 300);
        });
    });

    function escapeHtml(t) { var d = document.createElement('div'); d.textContent = t; return d.innerHTML; }
})();
</script>
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build success

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Views/ShiftAdmin/Index.cshtml
git commit -m "feat(slice3): add no-show UI, profile badges, voluntell, and staffing chart to dept shifts page"
```

---

## Chunk 5: Team Details & Profile No-Show History

### Task 10: Add shifts summary card to team details page

**Files:**
- Modify: `src/Humans.Web/Models/TeamViewModels.cs`
- Modify: `src/Humans.Web/Controllers/TeamController.cs`
- Modify: `src/Humans.Web/Views/Team/Details.cshtml`

- [ ] **Step 1: Add ShiftsSummary to TeamDetailViewModel**

In `TeamViewModels.cs`, add to `TeamDetailViewModel` after `PendingRequestCount`:

```csharp
public ShiftsSummaryCardViewModel? ShiftsSummary { get; set; }
```

- [ ] **Step 2: Add IShiftManagementService to TeamController**

Add field `private readonly IShiftManagementService _shiftMgmt;` and add it to the constructor parameter list and assignment.

- [ ] **Step 3: Load shifts summary in Details action**

In the `Details` action, after the `roleDefinitions` loading line and before creating `viewModel`, add:

```csharp
// Load shifts summary for departments (parent teams that aren't system teams)
ShiftsSummaryCardViewModel? shiftsSummary = null;
if (team.ParentTeamId == null && team.SystemTeamType == SystemTeamType.None)
{
    var es = await _shiftMgmt.GetActiveAsync();
    if (es != null)
    {
        var summaryData = await _shiftMgmt.GetShiftsSummaryAsync(es.Id, team.Id);
        if (summaryData != null)
        {
            var userId = user.Id;
            var canManageShifts = await _shiftMgmt.CanManageShiftsAsync(userId, team.Id);
            shiftsSummary = new ShiftsSummaryCardViewModel
            {
                TotalSlots = summaryData.TotalSlots,
                ConfirmedCount = summaryData.ConfirmedCount,
                PendingCount = summaryData.PendingCount,
                UniqueVolunteerCount = summaryData.UniqueVolunteerCount,
                ShiftsUrl = Url.Action("Index", "ShiftAdmin", new { slug })!,
                CanManageShifts = canManageShifts
            };
        }
    }
}
```

Then add `ShiftsSummary = shiftsSummary,` to the viewModel initialization.

Note: uses `_shiftMgmt.CanManageShiftsAsync` for the accurate shift management permission check, not the general `canManage` variable which covers team membership management.

- [ ] **Step 4: Render in Details.cshtml**

In `Views/Team/Details.cshtml`, in the right column (`col-md-4`), after the Resources card and before the Team Management card (before `@if (Model.CanCurrentUserManage)`), add:

```cshtml
@if (Model.ShiftsSummary != null)
{
    @await Html.PartialAsync("_ShiftsSummaryCard", Model.ShiftsSummary)
}
```

- [ ] **Step 5: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build success

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Models/TeamViewModels.cs src/Humans.Web/Controllers/TeamController.cs src/Humans.Web/Views/Team/Details.cshtml
git commit -m "feat(slice3): add shifts summary card to department team page"
```

---

### Task 11: Add no-show history to profile view

**Files:**
- Modify: `src/Humans.Web/Controllers/HumanController.cs`
- Modify: `src/Humans.Web/Views/Profile/Index.cshtml`

Note: `HumanController.View` renders `~/Views/Profile/Index.cshtml` explicitly. The spec's reference to `Views/Human/View.cshtml` is incorrect.

- [ ] **Step 1: Add services to HumanController**

Add fields:
```csharp
private readonly IShiftSignupService _shiftSignupService;
private readonly IShiftManagementService _shiftMgmt;
```

Add to constructor parameters and assignments.

- [ ] **Step 2: Load no-show history in View action**

In the `View` action, after `var isOwnProfile = viewer.Id == id;` and before creating the viewModel, add:

```csharp
// Load no-show history for coordinators/NoInfoAdmin/Admin viewing other profiles
List<NoShowHistoryItem>? noShowHistory = null;
if (!isOwnProfile)
{
    var viewerIsCoordinator = (await _shiftMgmt.GetCoordinatorDepartmentIdsAsync(viewer.Id)).Count > 0;
    var viewerIsNoInfoAdmin = User.IsInRole(RoleNames.NoInfoAdmin);
    var viewerIsAdmin = User.IsInRole(RoleNames.Admin);

    if (viewerIsCoordinator || viewerIsNoInfoAdmin || viewerIsAdmin)
    {
        var noShows = await _shiftSignupService.GetNoShowHistoryAsync(id);
        if (noShows.Count > 0)
        {
            var es = noShows[0].Shift.Rota.EventSettings;
            var tz = DateTimeZoneProviders.Tzdb[es.TimeZoneId];
            noShowHistory = noShows.Select(s =>
            {
                var shiftStart = s.Shift.GetAbsoluteStart(es);
                var zoned = shiftStart.InZone(tz);
                return new NoShowHistoryItem
                {
                    ShiftTitle = s.Shift.Title,
                    DepartmentName = s.Shift.Rota.Team?.Name ?? "",
                    ShiftDateLabel = zoned.ToString("ddd MMM d HH:mm", null),
                    MarkedByName = s.ReviewedByUser?.DisplayName,
                    MarkedAtLabel = s.ReviewedAt?.InZone(tz).ToString("MMM d HH:mm", null)
                };
            }).ToList();
        }
    }
}

ViewBag.NoShowHistory = noShowHistory;
```

- [ ] **Step 3: Render no-show history in Profile/Index.cshtml**

After the `<vc:profile-card ... />` tag and before the campaign grants section (`@if (Model.IsOwnProfile && Model.CampaignGrants.Any())`), add:

```cshtml
@{
    var noShowHistory = ViewBag.NoShowHistory as List<Humans.Web.Models.NoShowHistoryItem>;
}
@if (noShowHistory != null && noShowHistory.Count > 0)
{
    <div class="card mb-3">
        <div class="card-header">
            <h5 class="mb-0"><i class="fa-solid fa-user-xmark me-1"></i> No-Show History (@noShowHistory.Count)</h5>
        </div>
        <div class="card-body p-0">
            <table class="table table-sm mb-0">
                <thead>
                    <tr>
                        <th>Shift</th>
                        <th>Department</th>
                        <th>Date</th>
                        <th>Marked By</th>
                        <th>Marked At</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var item in noShowHistory)
                    {
                        <tr>
                            <td>@item.ShiftTitle</td>
                            <td>@item.DepartmentName</td>
                            <td>@item.ShiftDateLabel</td>
                            <td>@item.MarkedByName</td>
                            <td>@item.MarkedAtLabel</td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    </div>
}
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build success

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/HumanController.cs src/Humans.Web/Views/Profile/Index.cshtml
git commit -m "feat(slice3): add no-show history to volunteer profile view for coordinators/admin"
```

---

## Chunk 6: Final Build Verification

### Task 12: Full build and smoke test

- [ ] **Step 1: Full solution build**

Run: `dotnet build Humans.slnx`
Expected: Build success

- [ ] **Step 2: Run tests**

Run: `dotnet test Humans.slnx`
Expected: All tests pass

- [ ] **Step 3: Fix any remaining issues**

Address any compiler errors, missing usings, or type mismatches.

- [ ] **Step 4: Final commit if needed**

```bash
git add -p
git commit -m "fix(slice3): address build issues from integration"
```
