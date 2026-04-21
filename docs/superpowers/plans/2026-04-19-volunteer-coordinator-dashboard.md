# Volunteer Coordinator Dashboard Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `/Shifts/Dashboard` with overview counters, a departments staffing table that unfolds into subgroups (subteams with rotas, or period breakdown as fallback), coordinator activity list, and a trends chart; ship as a single PR with a local-only seeder.

**Architecture:** Add three new methods to `IShiftManagementService` (overview, coordinator activity, trends), new DTOs in `Humans.Application/DTOs/`. The controller renders a `ShiftDashboardViewModel` extended with the new panels. Views are partials rendered above existing content. Caching via `IMemoryCache` with invalidation hooks in `ShiftSignupService`. A new `DevelopmentDashboardSeeder` + `POST /dev/seed/dashboard` endpoint (gated to `IsDevelopment()` only) provides deterministic manual-test data including Infrastructure → Power / Plumbing subteams.

**Tech Stack:** .NET 9, EF Core, NodaTime, xUnit + AwesomeAssertions + NSubstitute, Bootstrap 5.3, Chart.js 4.5.

**Spec:** `docs/superpowers/specs/2026-04-19-volunteer-coordinator-dashboard-design.md`

---

## Chunk 1: DTOs, enum, interface extension

### Task 1.1: Add `TrendWindow` enum

**Files:**
- Create: `src/Humans.Application/Enums/TrendWindow.cs`

- [ ] **Step 1: Create the enum file**

```csharp
namespace Humans.Application.Enums;

public enum TrendWindow
{
    Last7Days,
    Last30Days,
    Last90Days,
    All
}
```

- [ ] **Step 2: Build to verify** — `dotnet build Humans.slnx`. Expected: success.

### Task 1.2: Add DTO records

**Files:**
- Create: `src/Humans.Application/DTOs/DashboardOverview.cs` (holds all dashboard records)

- [ ] **Step 1: Write records**

```csharp
using NodaTime;

namespace Humans.Application.DTOs;

public record DashboardOverview(
    int TotalShifts,
    int FilledShifts,
    PeriodBreakdown PeriodFillRates,
    int TicketHolderCount,
    int TicketHoldersEngaged,
    int NonTicketSignups,
    int StalePendingCount,
    IReadOnlyList<DepartmentStaffingRow> Departments);

public record PeriodBreakdown(double BuildPct, double EventPct, double StrikePct);

public record DepartmentStaffingRow(
    Guid DepartmentId,
    string DepartmentName,
    int TotalShifts,
    int FilledShifts,
    int SlotsRemaining,
    PeriodStaffing Build,
    PeriodStaffing Event,
    PeriodStaffing Strike,
    IReadOnlyList<SubgroupStaffingRow> Subgroups);

public record SubgroupStaffingRow(
    Guid? TeamId,
    string Name,
    bool IsDirect,
    int TotalShifts,
    int FilledShifts,
    int SlotsRemaining,
    PeriodStaffing Build,
    PeriodStaffing Event,
    PeriodStaffing Strike);

public record PeriodStaffing(int Total, int Filled, int SlotsRemaining);

public record CoordinatorActivityRow(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<CoordinatorLogin> Coordinators,
    int PendingSignupCount);

public record CoordinatorLogin(Guid UserId, string DisplayName, Instant? LastLoginAt);

public record DashboardTrendPoint(
    LocalDate Date,
    int NewSignups,
    int NewTicketSales,
    int DistinctLogins);
```

- [ ] **Step 2: Build** — `dotnet build Humans.slnx`. Expected: success.

### Task 1.3: Extend `IShiftManagementService`

**Files:**
- Modify: `src/Humans.Application/Interfaces/IShiftManagementService.cs`

- [ ] **Step 1: Add `using Humans.Application.DTOs; using Humans.Application.Enums;` at the top.**

- [ ] **Step 2: Append three method signatures** immediately above the `// === Shift Tags ===` section:

```csharp
/// <summary>
/// Gets the full dashboard overview (counters + per-department staffing rows with subgroup drill-down).
/// </summary>
Task<DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId);

/// <summary>
/// Gets per-team coordinator activity, scoped to teams with at least one pending signup.
/// </summary>
Task<IReadOnlyList<CoordinatorActivityRow>> GetCoordinatorActivityAsync(Guid eventSettingsId);

/// <summary>
/// Gets daily trend points (signups, ticket sales, distinct logins) for the window.
/// </summary>
Task<IReadOnlyList<DashboardTrendPoint>> GetDashboardTrendsAsync(Guid eventSettingsId, TrendWindow window);
```

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/Enums/TrendWindow.cs \
        src/Humans.Application/DTOs/DashboardOverview.cs \
        src/Humans.Application/Interfaces/IShiftManagementService.cs
git commit -m "Add dashboard DTOs, TrendWindow enum, and IShiftManagementService contract"
```

---

## Chunk 2: ShiftManagementService implementation (TDD)

### Task 2.1: Create test file with fixture

**Files:**
- Create: `tests/Humans.Application.Tests/Services/ShiftDashboardMetricsTests.cs`

- [ ] **Step 1: Scaffold test class** mirroring `ShiftUrgencyTests` setup (`HumansDbContext` with in-memory provider, `FakeClock`, service instance). Use `TestNow = Instant.FromUtc(2026, 7, 1, 12, 0)` and an `EventSettings` with `GateOpeningDate = LocalDate(2026, 8, 1)`, `BuildStartOffset = -14`, `EventEndOffset = 6`, `StrikeEndOffset = 9`, `TimeZoneId = "UTC"`, `IsActive = true`.

- [ ] **Step 2: Run to verify compiles** — `dotnet test tests/Humans.Application.Tests/ --filter FullyQualifiedName~ShiftDashboardMetricsTests`. Expected: no tests found, but compiles clean.

### Task 2.2: Counter basics test — empty database

- [ ] **Step 1: Add test**

```csharp
[Fact]
public async Task GetDashboardOverviewAsync_NoData_ReturnsZeroCounters()
{
    var es = await SeedActiveEventAsync();
    var result = await _service.GetDashboardOverviewAsync(es.Id);
    result.TotalShifts.Should().Be(0);
    result.FilledShifts.Should().Be(0);
    result.TicketHolderCount.Should().Be(0);
    result.TicketHoldersEngaged.Should().Be(0);
    result.NonTicketSignups.Should().Be(0);
    result.StalePendingCount.Should().Be(0);
    result.Departments.Should().BeEmpty();
}
```

- [ ] **Step 2: Run to verify fails** — Expected: build fails (method missing on service).

- [ ] **Step 3: Add stub in `ShiftManagementService`** that returns zero-valued record so test runs but fails with meaningful assertion.

### Task 2.3: Implement core overview query

**Files:**
- Modify: `src/Humans.Infrastructure/Services/ShiftManagementService.cs`

- [ ] **Step 1: Implement `GetDashboardOverviewAsync`** — cached via `_cache.GetOrCreate($"dashboard-overview:{eventSettingsId}", ...)` with 5-minute sliding TTL. Loads:
  - All visible shifts (`!shift.AdminOnly && shift.Rota.IsVisibleToVolunteers`) in the event, with `Include(s => s.Rota).ThenInclude(r => r.Team).ThenInclude(t => t.ParentTeam)`.
  - All confirmed signups for those shifts (project to `shiftId → confirmedCount` dict).
  - All paid `TicketOrder` matched to users (`PaymentStatus == Paid && MatchedUserId != null`) — project to `HashSet<Guid>` of ticket-holder user IDs.
  - All non-cancelled signups in the event — project to `HashSet<Guid>` of engaged user IDs.
  - Count stale-pending: `status == Pending && createdAt < now - 3 days`.

- [ ] **Step 2: Compute counters**:
  - `TotalShifts` = visible shifts count
  - `FilledShifts` = shifts where confirmed count ≥ `MinVolunteers`
  - `PeriodFillRates` = per-period filled / total (use `shift.GetShiftPeriod(es)`)
  - `TicketHolderCount` = ticket-holder user-ID count
  - `TicketHoldersEngaged` = intersection of ticket-holder + engaged user-ID sets
  - `NonTicketSignups` = engaged minus ticket-holder
  - `StalePendingCount` — query `ShiftSignups` directly, scoped to event via rota.

- [ ] **Step 3: Run empty-DB test** — Expected: PASS.

### Task 2.4: Filled-shift threshold tests

- [ ] **Step 1: Add three tests** covering `MinVolunteers - 1` (not filled), `=` (filled), `+1` (still filled). Build per-test: event + team + rota + 1 shift (`MinVolunteers = 3, MaxVolunteers = 5`) + N confirmed signups.

- [ ] **Step 2: Run** — Expected: PASS (implementation already handles this).

### Task 2.5: Ticket-holder classification tests

- [ ] **Step 1: Add tests**:
  - User with `PaymentStatus = Paid, MatchedUserId = U` → ticket holder (counted once even across multiple orders)
  - User with `PaymentStatus = Refunded` only → NOT ticket holder
  - User with signups and paid order → engaged
  - User with signups and no paid order → non-ticket signup
  - Ticket holder with only `Cancelled` signups → NOT engaged

- [ ] **Step 2: Run** — iterate until green. Commit after all pass:

```bash
git add tests/Humans.Application.Tests/Services/ShiftDashboardMetricsTests.cs \
        src/Humans.Infrastructure/Services/ShiftManagementService.cs
git commit -m "Dashboard overview counters with TDD coverage"
```

### Task 2.6: Department rows — no subteams

- [ ] **Step 1: Add test** — event with two parent teams (Gate, Kitchen), each with 1 rota and 2 shifts. Assert `Departments` contains two rows, sorted by fill % ascending, with `Subgroups` empty.

- [ ] **Step 2: Extend implementation** — group shifts by `shift.Rota.Team.ParentTeam ?? shift.Rota.Team` (aka "department"). For each group compute totals + per-period `PeriodStaffing`. Order rows by fill % ascending (ties by name).

- [ ] **Step 3: Run** — Expected: PASS.

### Task 2.7: Department rows — with subteams

- [ ] **Step 1: Add test**:
  - Parent team "Infrastructure", subteams "Power" (child of Infrastructure) and "Plumbing" (child of Infrastructure)
  - 1 rota on Infrastructure (direct) with 2 shifts
  - 1 rota on Power with 2 shifts
  - 1 rota on Plumbing with 2 shifts, low fill rate
  - Assert: one department row "Infrastructure" with `Subgroups.Count == 3`: first row is "Direct" (IsDirect=true) pinned top, then Plumbing (lowest fill %), then Power. Each subgroup's TotalShifts = 2. Aggregate assertion: sum(subgroup.TotalShifts) == department.TotalShifts; same for FilledShifts and Build/Event/Strike components.

- [ ] **Step 2: Extend service implementation** — after grouping by department, check whether any shift in the group belongs to a subteam (`rota.Team.ParentTeamId != null`). If yes, build `Subgroups`:
  - Per-subteam subgroup: sum over shifts where `rota.TeamId == subteamId`, use `rota.Team.Name`
  - "Direct" subgroup: sum over shifts where `rota.TeamId == departmentId` (if any)
  - Sort subgroups by fill % ascending, pin `IsDirect == true` to top
  - Else set `Subgroups = []`

- [ ] **Step 3: Run** — Expected: PASS.

- [ ] **Step 4: Add aggregate-invariant test**

```csharp
[Fact]
public async Task GetDashboardOverviewAsync_SubgroupsSumToDepartment()
{
    // Arrange: department with subteams, mixed fill rates
    // Assert: for each department with subgroups, totals and per-period counters sum exactly
}
```

- [ ] **Step 5: Run, commit**

```bash
git add tests/Humans.Application.Tests/Services/ShiftDashboardMetricsTests.cs \
        src/Humans.Infrastructure/Services/ShiftManagementService.cs
git commit -m "Department subgroup aggregation with subteam unfolding"
```

### Task 2.8: Stale-pending test

- [ ] **Step 1: Add test** — pending signup created exactly 3 days ago → not stale; 3 days + 1 minute → stale; confirmed with old createdAt → never stale.

- [ ] **Step 2: Run, adjust if needed, commit.**

### Task 2.9: Implement `GetCoordinatorActivityAsync`

- [ ] **Step 1: Add tests**:
  - Team with zero pending signups excluded
  - Team with multiple coordinators lists all
  - Rows sorted by oldest coordinator `LastLoginAt` first (nulls first), ties by team name

- [ ] **Step 2: Implement** — query pending signup counts per team (reuse `GetPendingShiftSignupCountsByTeamAsync`), filter teams with ≥1, load `TeamMembers.Where(tm => tm.Role == Coordinator)` with user, project to `CoordinatorActivityRow`. Cache with key `dashboard-coordinator-activity:{eventId}`, 5-min sliding.

- [ ] **Step 3: Run, commit**

```bash
git add …
git commit -m "Coordinator activity dashboard query with staleness ordering"
```

### Task 2.10: Implement `GetDashboardTrendsAsync`

- [ ] **Step 1: Add tests**:
  - Empty event with 7-day window → 7 points, all zero
  - One signup today → `NewSignups` = 1 on today's bucket, zero on prior days
  - Window boundaries: `Last30Days` returns exactly 30 points ending today (inclusive)
  - `TrendWindow.All` uses `EventSettings.CreatedAt` date as start

- [ ] **Step 2: Implement** — resolve start date from window + event TZ, generate list of `LocalDate`s, query three counts grouped by day in `EventSettings.TimeZoneId`, zero-fill missing days. Cache with key `dashboard-trends:{eventId}:{window}`, 5-min sliding.

- [ ] **Step 3: Run, commit**

```bash
git add …
git commit -m "Dashboard trends query with zero-filled buckets"
```

### Task 2.11: Cache invalidation on signup mutations

- [ ] **Step 1: Locate `ShiftSignupService` methods that create / change state** (`CreateSignupAsync`, `ConfirmSignupAsync`, etc.). Identify every place a signup row is inserted or status-transitioned.

- [ ] **Step 2: Inject `IMemoryCache`** if not already; add a private helper `InvalidateDashboardCaches(Guid eventSettingsId)` that removes `dashboard-overview:{id}` and `dashboard-coordinator-activity:{id}`. Call after successful save in each mutator.

- [ ] **Step 3: Run full test suite** — `dotnet test Humans.slnx`. Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add …
git commit -m "Invalidate dashboard caches on shift-signup mutations"
```

---

## Chunk 3: ViewModel + Controller

### Task 3.1: Extend `ShiftDashboardViewModel`

**Files:**
- Modify: `src/Humans.Web/Models/ShiftViewModels.cs`

- [ ] **Step 1: Add properties** to `ShiftDashboardViewModel`:

```csharp
public DashboardOverview? Overview { get; set; }
public List<CoordinatorActivityRow> CoordinatorActivity { get; set; } = [];
public List<DashboardTrendPoint> Trends { get; set; } = [];
public TrendWindow TrendWindow { get; set; } = TrendWindow.Last30Days;
```

Add `using Humans.Application.DTOs; using Humans.Application.Enums;` at the top.

### Task 3.2: Update controller

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftDashboardController.cs`

- [ ] **Step 1: Add `trendWindow` param**, default `Last30Days`. Parse from query string.

- [ ] **Step 2: Call the new service methods in parallel** (`Task.WhenAll` over the three). Populate `Overview`, `CoordinatorActivity`, `Trends`, `TrendWindow` on the view model.

- [ ] **Step 3: Build** — `dotnet build`. Commit:

```bash
git add src/Humans.Web/Models/ShiftViewModels.cs \
        src/Humans.Web/Controllers/ShiftDashboardController.cs
git commit -m "Wire dashboard controller to new overview/activity/trends methods"
```

---

## Chunk 4: Views (overview, departments, coordinator activity, trends)

### Task 4.1: Overview counters partial

**Files:**
- Create: `src/Humans.Web/Views/ShiftDashboard/_OverviewCounters.cshtml`

- [ ] **Step 1: Write partial** — accepts `DashboardOverview` as model. Five Bootstrap cards in a responsive grid (`row g-3`). Each card shows value + label + sub-line. Ticket-holders-engaged card gets `border-primary` emphasis and the sub-line "N humans haven't signed up" (uses "humans" per CLAUDE.md). Stale-pending card gets `border-warning` when count > 0. Shifts-filled card adds three period chips coloured by threshold (≥80% green, 60–79% amber, <60% red).

### Task 4.2: Departments table partial

**Files:**
- Create: `src/Humans.Web/Views/ShiftDashboard/_DepartmentsTable.cshtml`

- [ ] **Step 1: Write partial** — accepts `IReadOnlyList<DepartmentStaffingRow>`. Table with columns: department · total · filled · % · slots remaining · chevron. Each row has `data-bs-toggle="collapse" data-bs-target="#dept-<id>"`. Hidden row below shows:
  - If `row.Subgroups.Any()`: render inner table with one row per subgroup, columns: name (with "Direct" badge if `IsDirect`) · total · filled · % · slots · [Build / Event / Strike compact chips].
  - Else: render three rows (Build / Event / Strike) with total + filled + %.
- Period chips formatted as `@((int)Math.Round(100.0 * p.Filled / Math.Max(1, p.Total)))%` with same threshold colours as the overview.

### Task 4.3: Coordinator activity partial

**Files:**
- Create: `src/Humans.Web/Views/ShiftDashboard/_CoordinatorActivity.cshtml`

- [ ] **Step 1: Write partial** — accepts `IReadOnlyList<CoordinatorActivityRow>`. Table with columns: team · coordinators (comma-separated names) · last login (relative; red if > 7 days or null) · pending count. Hide entire card if list empty (`@if (Model.Any()) { ... }`).

### Task 4.4: Trends chart partial

**Files:**
- Create: `src/Humans.Web/Views/ShiftDashboard/_TrendsChart.cshtml`

- [ ] **Step 1: Write partial** — accepts a tuple `(IReadOnlyList<DashboardTrendPoint> Points, TrendWindow Window)`. Render a `<canvas id="dashboardTrendsChart">` and a window-toggle `<div>` with four `<a asp-action="Index" asp-route-trendWindow="...">` buttons. Each button gets `btn btn-outline-secondary` or `btn btn-primary active` depending on current window.

- [ ] **Step 2: Chart.js init script** at the bottom of the partial inside `<script>`. Three datasets: new signups, new ticket sales, DAU. Tooltip callback explains DAU limitation.

### Task 4.5: Wire partials into `Index.cshtml`

**Files:**
- Modify: `src/Humans.Web/Views/ShiftDashboard/Index.cshtml`

- [ ] **Step 1: Insert partials** between the filter bar and the existing staffing charts:

```razor
@if (Model.Overview is not null)
{
    @await Html.PartialAsync("_OverviewCounters", Model.Overview)
    @await Html.PartialAsync("_DepartmentsTable", Model.Overview.Departments)
}
@await Html.PartialAsync("_CoordinatorActivity", Model.CoordinatorActivity)
@await Html.PartialAsync("_TrendsChart", (Model.Trends, Model.TrendWindow))
```

- [ ] **Step 2: Run site locally** — `dotnet run --project src/Humans.Web`. Visit dashboard, verify markup renders without JS errors (empty state acceptable pre-seed). Stop the dev server.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/ShiftDashboard/
git commit -m "Render dashboard overview, departments, activity, trends partials"
```

### Task 4.6: Add localization strings

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.resx` + `.es.resx`, `.de.resx`, `.fr.resx`, `.it.resx`, `.ca.resx`

- [ ] **Step 1: Add keys** for each user-facing string added to the partials (e.g. `ShiftDash_Overview_ShiftsFilled`, `ShiftDash_Overview_TicketHoldersEngaged`, `ShiftDash_Overview_StalePending`, `ShiftDash_Departments_Title`, `ShiftDash_Coordinators_Title`, `ShiftDash_Trends_Title`, window labels). Use "humans" not "users/members/volunteers"; "humans" stays English in every locale.

- [ ] **Step 2: Rebuild, commit**

```bash
git add src/Humans.Web/Resources/
git commit -m "Add i18n strings for dashboard panels"
```

---

## Chunk 5: Seeder + dev endpoint

### Task 5.1: Write `DevelopmentDashboardSeeder`

**Files:**
- Create: `src/Humans.Web/Infrastructure/DevelopmentDashboardSeeder.cs`

- [ ] **Step 1: Class shell with `HumansDbContext` + `IClock` + `UserManager<User>` + logger injection.** Add `public async Task<DashboardSeedResult> SeedAsync(CancellationToken ct)`.

- [ ] **Step 2: Idempotency guard** — look for `EventSettings.Name == "Seeded Nowhere 2026 (dev)"`. If exists, return early with a message.

- [ ] **Step 3: Create `EventSettings`** — `GateOpeningDate = today + 60 days`, `BuildStartOffset = -14`, `EventEndOffset = 6`, `StrikeEndOffset = 9`, `IsActive = true`, `Name = "Seeded Nowhere 2026 (dev)"`.

- [ ] **Step 4: Create 6 parent teams** (Gate, Infrastructure, Kitchen, Medics, Rangers, DPW) if missing.

- [ ] **Step 5: Create Infrastructure's two subteams** — Power + Plumbing (`ParentTeamId = Infrastructure.Id`).

- [ ] **Step 6: Create rotas** — 3 per parent (one per period) + 1 per Infrastructure subteam (Event period on Power, Event period on Plumbing).

- [ ] **Step 7: Create shifts** — 8–12 per rota, mixed `MinVolunteers` / `MaxVolunteers` (2–8) and `DurationHours` (2–8).

- [ ] **Step 8: Create ~400 users** with profiles. ~300 with matched paid `TicketOrder`, ~30 ticket-less with profile, ~70 baseline.

- [ ] **Step 9: Assign coordinators** — Infrastructure gets 2 coordinators with `LastLoginAt = now - 9 days`; other departments' coordinators within 48h.

- [ ] **Step 10: Create signups** — Gate/Kitchen ≥85% confirmed, Strike rotas ~20% confirmed, Power subteam ~85%, Plumbing subteam ~40%, ~15 pending older than 3 days, a handful of Bailed / Refused.

- [ ] **Step 11: Spread `LastLoginAt` across 30 days** for DAU shape.

- [ ] **Step 12: SaveChangesAsync; return result record.**

### Task 5.2: Register seeder + add controller endpoint

**Files:**
- Modify: `src/Humans.Web/Program.cs`
- Modify: `src/Humans.Web/Controllers/DevSeedController.cs`

- [ ] **Step 1: `builder.Services.AddScoped<DevelopmentDashboardSeeder>();`** in Program.cs.

- [ ] **Step 2: Add action**:

```csharp
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
[HttpPost("dashboard")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SeedDashboard(CancellationToken ct)
{
    if (!_environment.IsDevelopment()) return NotFound();
    if (!IsDevAuthEnabled()) return NotFound();
    var (error, user) = await RequireCurrentUserAsync();
    if (error is not null) return error;

    var seeder = _serviceProvider.GetRequiredService<DevelopmentDashboardSeeder>();
    var result = await seeder.SeedAsync(ct);
    SetSuccess($"Dashboard seed complete: {result}");
    return RedirectToAction("Index", "ShiftDashboard");
}
```

Extract a `IsDevAuthEnabled()` helper that mirrors the config check used by `IsDevSeedEnabled` but is callable alongside the stricter `IsDevelopment()` gate.

- [ ] **Step 3: Add a seed button** on `/Shifts/Dashboard` visible only when `env.IsDevelopment()` (pass a flag via ViewData). Form POSTs to `/dev/seed/dashboard`.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Infrastructure/DevelopmentDashboardSeeder.cs \
        src/Humans.Web/Controllers/DevSeedController.cs \
        src/Humans.Web/Program.cs \
        src/Humans.Web/Views/ShiftDashboard/Index.cshtml
git commit -m "Add DevelopmentDashboardSeeder and /dev/seed/dashboard endpoint"
```

---

## Chunk 6: Docs + manual verification

### Task 6.1: Update feature doc

**Files:**
- Modify: `docs/features/25-shift-management.md`

- [ ] **Step 1: Append a "Coordinator Dashboard" section** with: purpose, metric definitions, subgroup rules, authorization, caching notes.

### Task 6.2: Update section invariants

**Files:**
- Modify: `docs/sections/Shifts.md`

- [ ] **Step 1: Add invariants**:
  - "All dashboard methods on `IShiftManagementService` require `ShiftDashboardAccess` policy at the controller; the service itself is auth-free per design rules."
  - "`DevelopmentDashboardSeeder` is gated to `IsDevelopment()` only; QA/preview/prod cannot run it."

### Task 6.3: Commit docs

- [ ] **Step 1:**

```bash
git add docs/features/25-shift-management.md docs/sections/Shifts.md
git commit -m "Document coordinator dashboard feature and section invariants"
```

### Task 6.4: Manual verification

- [ ] **Step 1: Launch** — `dotnet run --project src/Humans.Web`.

- [ ] **Step 2: Dev-login as Admin.**

- [ ] **Step 3: Click seed button** on `/Shifts/Dashboard`.

- [ ] **Step 4: Verify**:
  - Five counter cards populated (non-zero)
  - Infrastructure's row visible; expand shows "Direct" + Plumbing (top, low %) + Power
  - Gate row expands into three period rows
  - Coordinator activity row for Infrastructure shows red 9-days-ago
  - Trend chart populated; toggling 7/30/90/All updates the chart
  - Non-coordinator login gets 403 on the page

- [ ] **Step 5: Check build + tests final time** — `dotnet build Humans.slnx && dotnet test Humans.slnx`. Expected: all PASS, no new warnings.

---

## Execution handoff

Plan complete. This session executes the plan directly using superpowers:executing-plans (single-session batched execution with TDD discipline).
