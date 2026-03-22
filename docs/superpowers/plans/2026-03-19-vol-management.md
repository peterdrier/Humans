# Vol — Volunteer Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port the Figma Make volunteer management prototype into a new `/Vol` route with sub-navigation, coexisting with the current `/Shifts` section.

**Architecture:** Single `VolController : HumansControllerBase` with `[Route("Vol")]`, using existing `IShiftSignupService`, `IShiftManagementService`, and `ITeamService`. New view models in `Models/Vol/`, Razor views in `Views/Vol/` with a shared `_VolLayout.cshtml` sub-nav. No domain/infrastructure changes.

**Tech Stack:** ASP.NET Core MVC, Razor views, Bootstrap 5, Font Awesome 6, NodaTime, EF Core (existing services only)

**Spec:** `docs/superpowers/specs/2026-03-19-vol-volunteer-management-design.md`

**Worktree:** `.worktrees/vol-management` on branch `feature/vol-management`

---

## Task 1: Worktree, Scaffold & Nav Link

Create the worktree, controller shell, shared layout, and "V" nav link. End state: `/Vol` loads and redirects to `/Vol/MyShifts` (which shows a placeholder), nav link visible to privileged roles.

**Files:**
- Create: `src/Humans.Web/Controllers/VolController.cs`
- Create: `src/Humans.Web/Views/Vol/_VolLayout.cshtml`
- Create: `src/Humans.Web/Views/Vol/_ViewStart.cshtml`
- Create: `src/Humans.Web/Views/Vol/MyShifts.cshtml` (placeholder)
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml`

- [ ] **Step 1: Create worktree and branch**

```bash
git worktree add .worktrees/vol-management -b feature/vol-management
```

All subsequent commands run from `.worktrees/vol-management/`.

- [ ] **Step 2: Create the VolController shell**

Create `src/Humans.Web/Controllers/VolController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Humans.Domain.Entities;
using Humans.Application.Interfaces;
using Humans.Web.Authorization;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Vol")]
public class VolController : HumansControllerBase
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _signupService;
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;
    private readonly IGeneralAvailabilityService _availabilityService;
    private readonly ILogger<VolController> _logger;
    private readonly NodaTime.IClock _clock;

    public VolController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        ITeamService teamService,
        IProfileService profileService,
        IGeneralAvailabilityService availabilityService,
        UserManager<User> userManager,
        ILogger<VolController> logger,
        NodaTime.IClock clock)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _teamService = teamService;
        _profileService = profileService;
        _availabilityService = availabilityService;
        _logger = logger;
        _clock = clock;
    }

    [HttpGet("")]
    public IActionResult Index() => RedirectToAction(nameof(MyShifts));

    [HttpGet("MyShifts")]
    public IActionResult MyShifts() => View();
}
```

- [ ] **Step 3: Create `_ViewStart.cshtml`**

Create `src/Humans.Web/Views/Vol/_ViewStart.cshtml`:

```html
@{
    Layout = "~/Views/Vol/_VolLayout.cshtml";
}
```

- [ ] **Step 4: Create `_VolLayout.cshtml` with sub-nav**

Create `src/Humans.Web/Views/Vol/_VolLayout.cshtml`. This wraps the main `_Layout` and adds the page header + nav-pills sub-navigation. Reference the existing `_Layout.cshtml` nav patterns — use `RoleChecks`, `ShiftRoleChecks`, and `User.HasClaim(ActiveMemberClaimType, ActiveClaimValue)` for visibility checks. Use Font Awesome icons (`fa-solid fa-clipboard-list`, `fa-solid fa-calendar-days`, `fa-solid fa-users`, `fa-solid fa-bolt`, `fa-solid fa-chart-bar`, `fa-solid fa-gear`). Active tab determined by `ViewContext.RouteData`.

- [ ] **Step 5: Create placeholder `MyShifts.cshtml`**

Create `src/Humans.Web/Views/Vol/MyShifts.cshtml`:

```html
@{
    ViewData["Title"] = "My Shifts";
    ViewData["VolActiveTab"] = "MyShifts";
}

<div class="card">
    <div class="card-body text-center py-5">
        <i class="fa-solid fa-clipboard-list fa-2x text-muted mb-3"></i>
        <p class="text-muted">My Shifts page — coming next.</p>
    </div>
</div>
```

- [ ] **Step 6: Add "V" nav link to `_Layout.cshtml`**

In `src/Humans.Web/Views/Shared/_Layout.cshtml`, after the existing Shifts nav link, add:

```html
@if (RoleChecks.IsTeamsAdminBoardOrAdmin(User) || User.IsInRole(RoleNames.VolunteerCoordinator))
{
    <li class="nav-item">
        <a class="nav-link" asp-controller="Vol" asp-action="Index">V</a>
    </li>
}
```

- [ ] **Step 7: Build and verify**

```bash
dotnet build Humans.slnx
```

Expected: build succeeds, no errors.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/_VolLayout.cshtml \
        src/Humans.Web/Views/Vol/_ViewStart.cshtml \
        src/Humans.Web/Views/Vol/MyShifts.cshtml \
        src/Humans.Web/Views/Shared/_Layout.cshtml
git commit -m "feat(vol): scaffold controller, layout, and nav link"
```

---

## Task 2: View Models

Create all view model classes. These are flat projections — no business logic, just properties. Each page gets its own model.

**Files:**
- Create: `src/Humans.Web/Models/Vol/MyShiftsViewModel.cs`
- Create: `src/Humans.Web/Models/Vol/ShiftBrowserViewModel.cs`
- Create: `src/Humans.Web/Models/Vol/TeamsOverviewViewModel.cs`
- Create: `src/Humans.Web/Models/Vol/DepartmentDetailViewModel.cs`
- Create: `src/Humans.Web/Models/Vol/ChildTeamDetailViewModel.cs`
- Create: `src/Humans.Web/Models/Vol/UrgentShiftsViewModel.cs`
- Create: `src/Humans.Web/Models/Vol/ManagementViewModel.cs`
- Create: `src/Humans.Web/Models/Vol/SettingsViewModel.cs`

- [ ] **Step 1: Create `MyShiftsViewModel.cs`**

Properties: `List<MyShiftRow> Shifts`, `EventSettings EventSettings`. `MyShiftRow`: `Guid SignupId`, `string DutyTitle` (Rota.Name + Shift.Description), `string TeamName`, `Instant AbsoluteStart`, `Instant AbsoluteEnd`, `SignupStatus Status`, `bool CanBail` (Status is Confirmed or Pending). Reference existing `ShiftDisplayItem` for NodaTime patterns.

- [ ] **Step 2: Create `ShiftBrowserViewModel.cs`**

Properties: `EventSettings EventSettings`, `List<DepartmentShiftGroup> Departments` (reuse existing type from `ShiftViewModels.cs`), `List<DepartmentOption> AllDepartments`, `Guid? FilterDepartmentId`, `string? FilterDate`, `bool ShowFullShifts`, `HashSet<Guid> UserSignupShiftIds`, `Dictionary<Guid, SignupStatus> UserSignupStatuses`, `bool ShowSignups`, `bool IsPrivileged`. Follow the exact pattern from the existing `ShiftBrowseViewModel`.

- [ ] **Step 3: Create `TeamsOverviewViewModel.cs`**

Properties: `List<DepartmentCard> Departments`. `DepartmentCard`: `Guid TeamId`, `string Name`, `string Slug`, `string? Description`, `int ChildTeamCount`, `int TotalSlots`, `int FilledSlots`.

- [ ] **Step 4: Create `DepartmentDetailViewModel.cs`**

Properties: `Team Department`, `List<ChildTeamCard> ChildTeams`, `bool IsCoordinator`, `EventSettings EventSettings`. `ChildTeamCard`: `Guid TeamId`, `string Name`, `string Slug`, `int MemberCount`, `int TotalSlots`, `int FilledSlots`, `int PendingRequestCount`.

- [ ] **Step 5: Create `ChildTeamDetailViewModel.cs`**

Properties: `Team ChildTeam`, `Team Department`, `List<TeamMember> Members`, `List<RotaShiftGroup> Rotas` (reuse existing type), `List<TeamJoinRequest> PendingRequests`, `bool IsCoordinator`, `EventSettings EventSettings`, `HashSet<Guid> UserSignupShiftIds`, `Dictionary<Guid, SignupStatus> UserSignupStatuses`.

- [ ] **Step 6: Create `UrgentShiftsViewModel.cs`**

Properties: `List<UrgentShiftRow> Shifts`, `EventSettings EventSettings`. `UrgentShiftRow`: `Guid ShiftId`, `string DutyTitle`, `string TeamName`, `string TeamSlug`, `int DayOffset`, `LocalTime StartTime`, `Duration Duration`, `int Confirmed`, `int MaxVolunteers`, `ShiftPriority Priority`, `double UrgencyScore`. Reference existing `IShiftManagementService.GetUrgentShiftsAsync()` return type.

- [ ] **Step 7: Create `ManagementViewModel.cs`**

Properties: `bool SystemOpen`, `int? VolunteerCap`, `int ConfirmedVolunteerCount`, `EventSettings EventSettings`.

- [ ] **Step 8: Create `SettingsViewModel.cs`**

Properties: mirror `EventSettingsViewModel` from existing `ShiftViewModels.cs` — `Guid? Id`, `string EventName`, `string TimeZoneId`, `string GateOpeningDate`, `int BuildStartOffset`, `int EventEndOffset`, `int StrikeEndOffset`, `bool IsShiftBrowsingOpen`, `int? GlobalVolunteerCap`, `int ConfirmedVolunteerCount`. Add computed `int BuildDays`, `int EventDays`, `int StrikeDays`.

- [ ] **Step 9: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Models/Vol/
git commit -m "feat(vol): add all view models"
```

---

## Task 3: My Shifts Page

Wire up the controller action and build the view. First fully functional page.

**Files:**
- Modify: `src/Humans.Web/Controllers/VolController.cs`
- Modify: `src/Humans.Web/Views/Vol/MyShifts.cshtml`

- [ ] **Step 1: Implement MyShifts action**

In `VolController`, replace the placeholder `MyShifts()` action. Pattern:
1. `ResolveCurrentUserOrChallengeAsync()`
2. `_shiftMgmt.GetActiveAsync()` → guard `NoActiveEvent` view
3. `_signupService.GetByUserAsync(user.Id)` → get all signups
4. Map to `MyShiftsViewModel` with `MyShiftRow` list
5. `return View(model)`

Wrap in try-catch with `_logger.LogError`.

- [ ] **Step 2: Implement Bail POST action**

Add `[HttpPost("Bail")]` action:
1. `[ValidateAntiForgeryToken]`
2. Takes `Guid signupId`
3. Auth check: own signup OR `CanApproveSignupsAsync`
4. `_signupService.BailAsync(signupId, user.Id)`
5. `SetSuccess("Bailed from shift.")`
6. `RedirectToAction(nameof(MyShifts))`

- [ ] **Step 3: Build the MyShifts view**

Replace placeholder `MyShifts.cshtml`. Use Bootstrap card with table:
- Desktop: `<table class="table table-sm">` with columns: Duty, Team, Date & Time, Status, Action
- Status badges: `<span class="badge bg-success">Confirmed</span>`, `bg-warning` Pending, `bg-danger` Bailed, `bg-secondary` Refused/Cancelled/NoShow
- Bail button: `<form asp-action="Bail" method="post" class="d-inline">` with `@Html.AntiForgeryToken()`, confirmation via `onclick="return confirm('Bail from this shift?')"`
- Mobile: use `d-none d-md-table-row` / `d-md-none` pattern for responsive cards
- Empty state: "No shifts booked yet" with link to All Shifts

- [ ] **Step 4: Create `NoActiveEvent.cshtml` and `BrowsingClosed.cshtml`**

Create `src/Humans.Web/Views/Vol/NoActiveEvent.cshtml` and `BrowsingClosed.cshtml` — simple alert cards matching the existing `/Shifts/` equivalents.

- [ ] **Step 5: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/MyShifts.cshtml \
        src/Humans.Web/Views/Vol/NoActiveEvent.cshtml \
        src/Humans.Web/Views/Vol/BrowsingClosed.cshtml
git commit -m "feat(vol): implement My Shifts page with bail action"
```

---

## Task 4: All Shifts Page (Phase 1 Filters)

Shift browser with department filter, date filter, and open-only toggle. Rota cards grouped by department.

**Files:**
- Modify: `src/Humans.Web/Controllers/VolController.cs`
- Create: `src/Humans.Web/Views/Vol/Shifts.cshtml`
- Create: `src/Humans.Web/Views/Vol/_RotaCard.cshtml` (partial)
- Create: `src/Humans.Web/Views/Vol/_ShiftRow.cshtml` (partial)

- [ ] **Step 1: Implement Shifts GET action**

Add `[HttpGet("Shifts")]` action. Follow the existing `ShiftsController.Index` pattern closely:
1. Resolve user, check EventSettings, check browsing open
2. `_shiftMgmt.GetBrowseShiftsAsync(es.Id, departmentId, date, ...)` for shift data
3. `_shiftMgmt.GetDepartmentsWithRotasAsync(es.Id)` for department dropdown
4. Get user's signup IDs for signed-up indicators
5. Build `ShiftBrowserViewModel`, return View

- [ ] **Step 2: Implement SignUp POST action**

Add `[HttpPost("SignUp")]` action:
1. `[ValidateAntiForgeryToken]`, takes `Guid shiftId`
2. Auth: ActiveMember + browsing open (or privileged)
3. `_signupService.SignUpAsync(shiftId, user.Id)`
4. Handle result (success/warning/error)
5. `RedirectToAction(nameof(Shifts))`

- [ ] **Step 3: Create `_RotaCard.cshtml` partial**

Partial for a single rota card. Receives a `RotaShiftGroup` model. Shows:
- Card with header: rota name + priority badge
- Description (if any)
- Fill bar: `<div class="progress">` with percentage
- Slots summary: "X / Y filled"
- Collapsible shift rows via Bootstrap collapse (`data-bs-toggle="collapse"`)

- [ ] **Step 4: Create `_ShiftRow.cshtml` partial**

Partial for a single shift row within a rota. Shows:
- Date (formatted from DayOffset + EventSettings.GateOpeningDate)
- Time range
- Volunteers filled/total
- Priority badge
- Policy badge (Public=green "Instant", RequireApproval=amber "Approval")
- Sign Up / Bail / "Signed up" / "Full" button

- [ ] **Step 5: Build `Shifts.cshtml` view**

Assembles filter panel + department sections + rota cards:
- Filter form (GET): department `<select>`, date `<input type="date">`, open-only `<input type="checkbox">`, clear link
- Summary bar: rota count, shift count, open slots
- Loop departments → render `_RotaCard` partials
- Empty state for no results

- [ ] **Step 6: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/Shifts.cshtml \
        src/Humans.Web/Views/Vol/_RotaCard.cshtml \
        src/Humans.Web/Views/Vol/_ShiftRow.cshtml
git commit -m "feat(vol): implement All Shifts page with Phase 1 filters"
```

---

## Task 5: Teams Overview

Department cards grid.

**Files:**
- Modify: `src/Humans.Web/Controllers/VolController.cs`
- Create: `src/Humans.Web/Views/Vol/Teams.cshtml`

- [ ] **Step 1: Implement Teams GET action**

Add `[HttpGet("Teams")]`. Load parent teams (ParentTeamId == null, has rotas), aggregate staffing data per department, build `TeamsOverviewViewModel`.

- [ ] **Step 2: Build `Teams.cshtml` view**

Grid of Bootstrap cards (`row row-cols-1 row-cols-md-2 row-cols-lg-3 g-4`). Each card:
- Card header: department name
- Card body: description, child team count, fill bar
- Link to Department Detail: `asp-action="DepartmentDetail" asp-route-slug="@dept.Slug"`
- Empty state for departments with no rotas

- [ ] **Step 3: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/Teams.cshtml
git commit -m "feat(vol): implement Teams Overview page"
```

---

## Task 6: Department Detail

Child teams listed under a department with staffing data.

**Files:**
- Modify: `src/Humans.Web/Controllers/VolController.cs`
- Create: `src/Humans.Web/Views/Vol/DepartmentDetail.cshtml`

- [ ] **Step 1: Implement DepartmentDetail GET action**

Add `[HttpGet("Teams/{slug}")]`. Load team by slug, verify it's a parent team (ParentTeamId == null), load child teams, staffing data, pending requests (if coordinator). Build `DepartmentDetailViewModel`.

- [ ] **Step 2: Build `DepartmentDetail.cshtml` view**

- Breadcrumb: Teams → Department Name
- Department header with description
- Child team cards: name, member count, fill stats
- Each card links to `ChildTeamDetail`
- Coordinator actions (if authorized): link to create rota (at department level)

- [ ] **Step 3: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/DepartmentDetail.cshtml
git commit -m "feat(vol): implement Department Detail page"
```

---

## Task 7: Child Team Detail

Full team view with rotas, shifts, members, and coordinator actions.

**Files:**
- Modify: `src/Humans.Web/Controllers/VolController.cs`
- Create: `src/Humans.Web/Views/Vol/ChildTeamDetail.cshtml`

- [ ] **Step 1: Implement ChildTeamDetail GET action**

Add `[HttpGet("Teams/{parentSlug}/{childSlug}")]`. Load parent team by `parentSlug`, child team by `childSlug`, verify child belongs to parent. Load members, rotas (filtered to this team's shifts), pending requests, user signup data. Check coordinator status. Build `ChildTeamDetailViewModel`.

- [ ] **Step 2: Implement coordinator POST actions**

Add Approve, Refuse, NoShow POST actions (if not already added):
- `[HttpPost("Approve")]` with `Guid signupId` — `_signupService.ApproveAsync`
- `[HttpPost("Refuse")]` with `Guid signupId` — `_signupService.RefuseAsync`
- `[HttpPost("NoShow")]` with `Guid signupId` — `_signupService.MarkNoShowAsync`

All gated by `CanApproveSignupsAsync`.

- [ ] **Step 3: Build `ChildTeamDetail.cshtml` view**

- Breadcrumb: Teams → Department → Team Name
- Team header with description, member count
- Member roster table (name, role, joined date)
- Rotas section: reuse `_RotaCard` partial, filtered to this team
- Pending requests (if coordinator): list with approve/reject buttons
- Coordinator action buttons for shift management

- [ ] **Step 4: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/ChildTeamDetail.cshtml
git commit -m "feat(vol): implement Child Team Detail page with coordinator actions"
```

---

## Task 8: Urgent Shifts

Urgency-sorted table with volunteer search and voluntell.

**Files:**
- Modify: `src/Humans.Web/Controllers/VolController.cs`
- Create: `src/Humans.Web/Views/Vol/Urgent.cshtml`

- [ ] **Step 1: Implement Urgent GET action**

Add `[HttpGet("Urgent")]`. Guard: `CanAccessDashboard` check. Load `_shiftMgmt.GetUrgentShiftsAsync()`, map to `UrgentShiftsViewModel`.

- [ ] **Step 2: Implement Voluntell POST action**

Add `[HttpPost("Voluntell")]` with `Guid shiftId`, `Guid userId`:
1. Guard: `CanAccessDashboard`
2. `_signupService.VoluntellAsync(shiftId, userId, enrolledByUserId: user.Id)`
3. `SetSuccess("Volunteer assigned.")`
4. Redirect back

- [ ] **Step 3: Implement volunteer search endpoint**

Add `[HttpGet("SearchVolunteers")]` returning JSON. Reuse existing `ShiftVolunteerSearchBuilder` pattern from `ShiftDashboardController`. Takes `string? query`, `Guid shiftId`. Returns list of volunteer results with inline assign forms.

- [ ] **Step 4: Build `Urgent.cshtml` view**

- Section header with bolt icon
- Table: Urgency bar, Duty (with tooltip), Team, Date & Time, Capacity bar, Priority badge, "Find volunteer" button
- "Find volunteer" opens a simple search form (can be inline or modal using Bootstrap modal)
- Search results show volunteer name, skills, booked shift count, "Assign" button
- Mobile: card layout instead of table
- Empty state: "All duties are fully staffed!"

- [ ] **Step 5: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/Urgent.cshtml
git commit -m "feat(vol): implement Urgent Shifts page with voluntell"
```

---

## Task 9: Management Dashboard

Manager-only dashboard with system status, cap indicator, and placeholder export buttons.

**Files:**
- Modify: `src/Humans.Web/Controllers/VolController.cs`
- Create: `src/Humans.Web/Views/Vol/Management.cshtml`

- [ ] **Step 1: Implement Management GET action**

Add `[HttpGet("Management")]`. Guard: `CanAccessDashboard`. Load EventSettings, confirmed volunteer count. Build `ManagementViewModel`.

- [ ] **Step 2: Build `Management.cshtml` view**

- System status banner: card with green/red accent showing Open/Closed, links to Settings
- Volunteer Cap card: progress bar with thresholds (75% amber, 90% red), confirmed count / cap
- Actions grid: 3 export buttons (disabled with "Phase 3" tooltip) + link to Settings
- Use Bootstrap alert variants for status coloring

- [ ] **Step 3: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/Management.cshtml
git commit -m "feat(vol): implement Management dashboard"
```

---

## Task 10: Settings Page

Event settings editor with system toggle and volunteer cap.

**Files:**
- Modify: `src/Humans.Web/Controllers/VolController.cs`
- Create: `src/Humans.Web/Views/Vol/Settings.cshtml`

- [ ] **Step 1: Implement Settings GET action**

Add `[HttpGet("Settings")]`. Guard: Admin only. Load EventSettings, confirmed volunteer count. Build `SettingsViewModel`.

- [ ] **Step 2: Implement Settings POST action**

Add `[HttpPost("Settings")]` with `[ValidateAntiForgeryToken]`. Guard: Admin only. Update `IsShiftBrowsingOpen` and `GlobalVolunteerCap` on the existing EventSettings. `SetSuccess`. Redirect.

- [ ] **Step 3: Build `Settings.cshtml` view**

- Event Periods timeline: three styled cards showing Build/Event/Strike date ranges
- System toggle: checkbox or toggle switch with confirmation JS for closing
- Volunteer cap: number input with progress bar showing current fill
- Access Matrix: static table showing feature × role grid
- All wrapped in a `<form>` posting to Settings

- [ ] **Step 4: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/Settings.cshtml
git commit -m "feat(vol): implement Settings page"
```

---

## Task 11: Registration Placeholder

Static page with "coming soon" messaging.

**Files:**
- Modify: `src/Humans.Web/Controllers/VolController.cs`
- Create: `src/Humans.Web/Views/Vol/Register.cshtml`

- [ ] **Step 1: Implement Register GET action**

Add `[HttpGet("Register")]`. No auth required (public-facing). Return view with no model.

- [ ] **Step 2: Build `Register.cshtml` view**

Visual placeholder matching the Figma welcome screen:
- Centered card with heart icon
- "Volunteer with Elsewhere" heading
- Period overview cards (Build/Event/Strike) — static content
- Prominent alert: "Volunteer registration is being redesigned — use the existing signup flow for now."
- Link to existing shift browser and profile page

Note: this page does NOT use `_VolLayout.cshtml` sub-nav (it's standalone). Override layout in the view: `@{ Layout = "~/Views/Shared/_Layout.cshtml"; }`.

- [ ] **Step 3: Build and commit**

```bash
dotnet build Humans.slnx
git add src/Humans.Web/Controllers/VolController.cs \
        src/Humans.Web/Views/Vol/Register.cshtml
git commit -m "feat(vol): add Registration placeholder page"
```

---

## Task 12: Polish & Verify

Final pass: empty states, error handling, build verification.

**Files:**
- Various views and controller

- [ ] **Step 1: Verify all empty states**

Check each page renders correctly with:
- No EventSettings → NoActiveEvent view
- Browsing closed → BrowsingClosed view (for non-privileged on All Shifts)
- Zero data in each list → appropriate empty state message

- [ ] **Step 2: Error handling sweep**

Verify all controller actions have try-catch with `_logger.LogError`. Verify all POST actions have `[ValidateAntiForgeryToken]`.

- [ ] **Step 3: Full build and format check**

```bash
dotnet build Humans.slnx
dotnet format --verify-no-changes Humans.slnx
```

Fix any formatting issues.

- [ ] **Step 4: Final commit**

```bash
git add -u src/Humans.Web/Controllers/VolController.cs \
           src/Humans.Web/Views/Vol/
git commit -m "feat(vol): polish empty states and error handling"
```

---

## Summary

| Task | Page | Key Actions |
|------|------|-------------|
| 1 | Scaffold | Controller, layout, nav link |
| 2 | View Models | All 8 view model classes |
| 3 | My Shifts | GET + Bail POST |
| 4 | All Shifts | GET + SignUp POST + filters |
| 5 | Teams Overview | GET |
| 6 | Department Detail | GET |
| 7 | Child Team Detail | GET + Approve/Refuse/NoShow POST |
| 8 | Urgent Shifts | GET + Voluntell POST + volunteer search |
| 9 | Management | GET (dashboard) |
| 10 | Settings | GET + POST |
| 11 | Registration | GET (placeholder) |
| 12 | Polish | Empty states, error handling, format |
