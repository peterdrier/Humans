# Vol — Volunteer Management Section

**Date:** 2026-03-19
**Status:** Approved
**Source:** Figma Make prototype — [Humans Volunteers Management](https://www.figma.com/make/mJ3ufiTTk8cHMvLB76ysAw/Humans-Volunteers-Management)

## Summary

Port the full `/volunteer-management` section from the Figma Make prototype into a new `/Vol` route in the Humans ASP.NET app. This reorganizes existing volunteer shift management features into a cohesive sub-section with its own sub-navigation, while coexisting with the current `/Shifts` section until a future swap.

## Decisions

| Decision | Choice |
|----------|--------|
| Scope | Full Figma prototype — all pages |
| Route prefix | `/Vol` |
| Parallel rollout | Nav link visible to Admin + all coordinator roles only |
| Styling | Bootstrap with standard colors; Figma layout/UX. Tailwind + earth tones deferred to Phase 2 |
| Role mapping | Direct mapping to existing claims |
| Landing page | `/Vol` → redirects to `/Vol/MyShifts` |
| Controller | New single `VolController` |
| Teams in /Vol | Full team views within /Vol |
| Settings & Registration | Both inside /Vol, auth-gated |
| Sub-nav | Bootstrap `nav-pills` with icons, role-gated tabs |
| Isolation | Own worktree and feature branch |

## Architecture

### Controller

`VolController : HumansControllerBase` with `[Route("Vol")]` prefix.

- Uses existing services: `IShiftSignupService`, `IShiftManagementService`, `ITeamService`
- New view models in `Models/Vol/` — flat projections, no business logic
- Views in `Views/Vol/`

### Nav Integration

New "V" link in `_Layout.cshtml`:
- Position: alongside existing nav items
- Label: "V" (short, non-confusing test label)
- Visibility: `IsTeamsAdminBoardOrAdmin || VolunteerCoordinator` (covers Admin, Board, TeamsAdmin, VolunteerCoordinator)
- Existing `/Shifts` nav stays untouched — both coexist

### Shared Layout

`Views/Vol/_VolLayout.cshtml` — rendered by all Vol actions:
- Page header: "Volunteers Management" / "Shifts, duties, staffing coordination"
- Bootstrap `nav-pills` sub-navigation with role-gated tabs

| Tab | Route | Visible To |
|-----|-------|-----------|
| My Shifts | `/Vol/MyShifts` | All authenticated |
| All Shifts | `/Vol/Shifts` | ActiveMember (respects `IsShiftBrowsingOpen`) |
| Teams | `/Vol/Teams` | ActiveMember |
| Urgent Shifts | `/Vol/Urgent` | NoInfoAdmin, Admin, VolunteerCoordinator |
| Management | `/Vol/Management` | Admin, VolunteerCoordinator |
| Settings | `/Vol/Settings` | Admin |

Registration (`/Vol/Register`) is standalone — not in sub-nav.

### Role Mapping

| Figma Role | Humans Role/Claim |
|-----------|------------------|
| volunteer | `ActiveMember` custom claim (checked via `User.HasClaim(ActiveMemberClaimType, ActiveClaimValue)`, NOT `IsInRole`) |
| lead | Team Coordinator (TeamMemberRole.Coordinator on specific team) |
| metalead | Coordinator of a ParentTeam (team with ChildTeams) |
| noinfo | NoInfoAdmin global role |
| manager | Admin or VolunteerCoordinator global role |

Users see views based on their actual roles/claims — no role switcher in production.

## Pages

### My Shifts (`GET /Vol/MyShifts`)

Current user's shift signups in a Bootstrap card with table layout.

**Columns:**

| Column | Source |
|--------|--------|
| Duty | `Shift.Rota.Name` + `Shift.Description` |
| Team | `Shift.Rota.Team.Name` |
| Date & Time | Computed from `Shift.DayOffset`, `Shift.StartTime`, `Shift.Duration`, `EventSettings.GateOpeningDate` |
| Status | `ShiftSignup.Status` — badge: Confirmed=green, Pending=amber, Bailed=red, Refused=gray, Cancelled=gray, NoShow=red |
| Action | Bail button on Confirmed and Pending signups (with confirmation modal) |

**Data:** `IShiftSignupService.GetByUserAsync(userId)` joined to Shift/Rota.
**Mobile:** Stacked card layout (duty+team top, status badge right, date+bail bottom).

### All Shifts (`GET /Vol/Shifts`)

Shift browser with filters and rota cards grouped by department.

**Filter panel — Phase 1 (matches existing `GetBrowseShiftsAsync` params):**
- Department dropdown (single select)
- Date filter (single day)
- Open Only toggle (default: on)

**Filter panel — Future phases (requires new service params or in-memory filtering + JS):**
- Text search, multi-select departments/teams, period/priority chips, day selector, cascading filters, active filter count

**Summary bar:** X rotas, Y shifts, Z open slots, W essential.

**Results:** Grouped by parent team, then by rota. Each rota is a Bootstrap card:
- Team label, rota name (from `Rota.Name`), description (`Rota.Description`)
- Fill bar (slots filled / total across all shifts in rota)
- Expandable "Show shifts" → shift rows:
  - Date (from DayOffset), Time, Volunteers (filled/total), Priority badge, Policy (Public=Instant, RequireApproval=Approval), Sign Up / Bail button

**Data:** `IShiftManagementService.GetBrowseShiftsAsync()` with existing filter params. Rota metadata from `GetRotasByDepartmentAsync()`.

**POST actions:**
- `POST /Vol/SignUp` → `IShiftSignupService.SignUpAsync`
- `POST /Vol/Bail` → `IShiftSignupService.BailAsync`

### Teams Overview (`GET /Vol/Teams`)

Grid of department (parent team) cards.

Each card: name, description, child team count, aggregate shift fill stats (from existing staffing data).

**Data:** `ITeamService` for teams, `IShiftManagementService.GetStaffingDataAsync()` for fill stats. Team accent colors and lead names are not in the data model — use uniform card styling for Phase 1.

### Department Detail (`GET /Vol/Teams/{slug}`)

Breadcrumb: "All Departments" → department name.

Lists child teams as cards with:
- Team name, member count
- Rota fill rates per rota
- Pending join requests count (if coordinator)
- Coordinator actions (if authorized): create rota (department-level — rotas belong to parent teams, not child teams), manage members

**Data:** `ITeamService.GetTeamBySlugAsync()`, `IShiftManagementService.GetRotasByDepartmentAsync()`, `IShiftManagementService.GetStaffingDataAsync()`.

### Child Team Detail (`GET /Vol/Teams/{parentSlug}/{childSlug}`)

Full team view:
- Team info header (name, description, member count)
- Member roster with role assignments
- Rotas section: shows rotas from the parent department filtered to shifts relevant to this child team, with shift grid (day × time), fill indicators, rota metadata (description, practical info)
- Pending join requests (if coordinator)
- Coordinator actions: create/edit shift within existing rotas, approve/refuse signups, voluntell, manage members. Rota CRUD is at department level (see Department Detail).

**Data:** Same services as Department Detail, plus `IShiftSignupService` for signup management.

### Urgent Shifts (`GET /Vol/Urgent`)

Table of unfilled shifts sorted by urgency score.

**Urgency score:** `priority_weight × remaining_slots` (uses existing `IShiftManagementService.CalculateScore`).

**Columns:**

| Column | Description |
|--------|-------------|
| Urgency | Visual bar showing relative score |
| Duty | Shift title with info tooltip for description |
| Team | Team name |
| Date & Time | Formatted from DayOffset + times |
| Capacity | Fill bar + "X left" |
| Priority | Badge (Normal/Important/Essential) |
| Action | "Find volunteer" button |

**"Find volunteer" flow — Phase 1:**
Reuse existing `ShiftVolunteerSearchBuilder` pattern: single search endpoint returning results with inline assign buttons. The existing `VolunteerSearchResult` record already includes skills, quirks, languages, dietary, booked shift count, overlap detection, and medical (when authorized).

**"Find volunteer" flow — Future phase:**
Nested modals (search → profile detail → assign) with richer filtering by skill and team.

**Data:** `IShiftManagementService.GetUrgentShiftsAsync()`. Volunteer search via existing `ShiftVolunteerSearchBuilder`.

### Management (`GET /Vol/Management`)

Manager dashboard:
- System status banner (Open/Closed link to Settings)
- Global Volunteer Cap indicator (progress bar, amber at 75%, red at 90%)
- Actions grid:
  - Export All Rotas CSV → `GET /Vol/Export/Rotas` (placeholder — column definitions in Phase 3)
  - Export Early Entry CSV → `GET /Vol/Export/EarlyEntry` (placeholder — column definitions in Phase 3)
  - Export Cantina CSV → `GET /Vol/Export/Cantina` (placeholder — column definitions in Phase 3)
  - Link to Event Settings

**Data:** `EventSettings` for system status and cap. Confirmed volunteer count from signup queries.

### Settings (`GET /Vol/Settings`, `POST /Vol/Settings`)

- Event Periods timeline: Build/Event/Strike date ranges derived from EventSettings offsets + GateOpeningDate
- System Open/Close toggle (`IsShiftBrowsingOpen`) with confirmation modal for closing
- Global Volunteer Cap input with progress indicator and threshold warnings
- Access Matrix reference table (static)

**Data:** `EventSettings` entity — read and update.

### Registration (`GET /Vol/Register`) — PLACEHOLDER

**Status:** UI only, not wired to backend. Page renders the wizard screens but the submit action is disabled with a "Coming soon" message.

The Figma prototype has a multi-step registration wizard (welcome → availability → periods → path choice → team picker → confirmation). The existing app handles volunteer registration differently (profile + consent + auto-approval to Volunteers team, with `GeneralAvailability` for day-level availability).

This page will be built as a visual placeholder to match the Figma design, with a prominent subtext: *"Volunteer registration is being redesigned — use the existing signup flow for now."* The backend integration requires design decisions about how the wizard maps to existing entities, which is deferred.

**Note:** This supplements (does not replace) existing membership onboarding.

## Data Flow

### Service Reuse

No new Application layer interfaces. All pages wire into existing services:

- `IShiftSignupService` — signup lifecycle (sign up, bail, approve, refuse, voluntell, cancel, no-show)
- `IShiftManagementService` — shift browsing, urgency scoring, staffing data, rota/shift CRUD, event settings
- `ITeamService` — team hierarchy, members, join requests, role definitions

### New View Models

Each page gets a dedicated view model in `Models/Vol/`:

- `MyShiftsViewModel` — flattened shift signup rows
- `ShiftBrowserViewModel` — filter state + rota groups with shifts
- `TeamsOverviewViewModel` — department cards with aggregate stats
- `DepartmentDetailViewModel` — child teams with staffing data
- `ChildTeamDetailViewModel` — full team view with rotas, shifts, members
- `UrgentShiftsViewModel` — urgency-sorted shift list
- `ManagementViewModel` — system status, cap data, export links
- `SettingsViewModel` — event settings form
- `RegistrationViewModel` — placeholder (static page, no backend wiring)

### Authorization

Claims-first pattern (per CODING_RULES):
- Controller actions: `[Authorize]` + `RoleChecks` helpers
- Sub-nav visibility: `@if` blocks in `_VolLayout.cshtml`
- Team-specific ops: fallback to `IShiftManagementService.IsDeptCoordinatorAsync` / `CanManageShiftsAsync`

### Error Handling

- All controller actions: try-catch with `ILogger` logging
- TempData flash messages via `SetSuccess()` / `SetError()`
- Bail/signup actions: confirmation modals before POST

## POST Actions

All POST endpoints require `[ValidateAntiForgeryToken]` per codebase convention.

| Action | Route | Auth Required | Service Call |
|--------|-------|--------------|-------------|
| Sign up | `POST /Vol/SignUp` | ActiveMember + `IsShiftBrowsingOpen` (or privileged role) | `IShiftSignupService.SignUpAsync` |
| Bail | `POST /Vol/Bail` | ActiveMember (own signup) or CanApproveSignups (any signup) | `IShiftSignupService.BailAsync` |
| Approve signup | `POST /Vol/Approve` | `CanApproveSignupsAsync` (VolCoord, DeptCoordinator, Admin) | `IShiftSignupService.ApproveAsync` |
| Refuse signup | `POST /Vol/Refuse` | `CanApproveSignupsAsync` (VolCoord, DeptCoordinator, Admin) | `IShiftSignupService.RefuseAsync` |
| Voluntell | `POST /Vol/Voluntell` | `CanAccessDashboard` (Admin, NoInfoAdmin, VolCoord) | `IShiftSignupService.VoluntellAsync` |
| Mark no-show | `POST /Vol/NoShow` | `CanApproveSignupsAsync` (VolCoord, DeptCoordinator, Admin) | `IShiftSignupService.MarkNoShowAsync` |
| Update settings | `POST /Vol/Settings` | Admin only | Update `EventSettings` |
| Register | `POST /Vol/Register` | Deferred (placeholder page) | — |
| Export Rotas | `GET /Vol/Export/Rotas` | Deferred (Phase 3 — column defs TBD) | — |
| Export Early Entry | `GET /Vol/Export/EarlyEntry` | Deferred (Phase 3 — column defs TBD) | — |
| Export Cantina | `GET /Vol/Export/Cantina` | Deferred (Phase 3 — column defs TBD) | — |

## Empty States & Edge Cases

- **No active EventSettings:** Redirect to a "No active event" message (matching existing `ShiftsController` pattern)
- **Shift browsing closed:** All Shifts tab shows "Browsing is currently closed" for non-privileged users (matching existing `BrowsingClosed` view)
- **Zero signups (My Shifts):** "No shifts booked yet" centered message with link to All Shifts
- **Zero rotas matching filters (All Shifts):** "No rotas match your filters" with clear-filters link
- **Zero departments with rotas (Teams):** Show departments but with "No rotas configured" on empty ones
- **Zero urgent shifts:** "All duties are fully staffed!" success message
- **Zero volunteer search results:** "No volunteers match your search" with avatar icon

## Implementation Notes

- **Controller size:** Single `VolController` is fine even at 1000+ lines. Split only if it becomes genuinely hard to navigate, not for arbitrary line count thresholds
- **`_ViewStart.cshtml`:** Use `Views/Vol/_ViewStart.cshtml` to set `Layout = "_VolLayout"` so individual views don't need to declare it
- **NodaTime:** View models should use `LocalDate`, `LocalTime`, `Duration` from NodaTime for date/time fields; formatting happens in the Razor views
- **Localization:** Vol pages are user-facing — use existing localization patterns (`IStringLocalizer<SharedResource>`) for display strings
- **Icons:** Font Awesome 6 only (`fa-solid fa-*`). Bootstrap Icons are NOT loaded in this project (per CODING_RULES). Map Figma's Lucide icons to FA6 equivalents

## New Files

| Category | Files |
|----------|-------|
| Controller | `Controllers/VolController.cs` |
| Shared layout | `Views/Vol/_VolLayout.cshtml` |
| Page views | `Views/Vol/MyShifts.cshtml`, `Shifts.cshtml`, `Teams.cshtml`, `DepartmentDetail.cshtml`, `ChildTeamDetail.cshtml`, `Urgent.cshtml`, `Management.cshtml`, `Settings.cshtml`, `Register.cshtml` |
| View models | `Models/Vol/MyShiftsViewModel.cs`, `ShiftBrowserViewModel.cs`, `TeamsOverviewViewModel.cs`, `DepartmentDetailViewModel.cs`, `ChildTeamDetailViewModel.cs`, `UrgentShiftsViewModel.cs`, `ManagementViewModel.cs`, `SettingsViewModel.cs`, `RegistrationViewModel.cs` |
| Partials | `Views/Vol/_ShiftRow.cshtml`, `_RotaCard.cshtml`, `_TeamCard.cshtml`, `_FilterPanel.cshtml`, `_UrgencyBar.cshtml` (shared partials for reuse across pages) |

## Modified Files

| File | Change |
|------|--------|
| `Views/Shared/_Layout.cshtml` | Add "V" nav link with role gate |

## No Changes To

- Domain entities
- Application interfaces
- Infrastructure/data layer
- Existing controllers/views
- Database schema

## Future Phases

- **Phase 2:** Tailwind CSS + Figma earth-tone palette reskin (CSS-only change, same Razor views)
- **Phase 3:** CSV export column definitions + implementation; advanced filter panel (multi-select, cascading, JS); nested volunteer search modals; registration backend integration; React/Blazor interactivity (optional)
- **Swap:** When `/Vol` is validated, replace `/Shifts` nav link → `/Vol`, remove "V" label, make `/Vol` the primary volunteering section
