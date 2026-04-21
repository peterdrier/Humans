# Volunteer Coordinator Dashboard — Design

**Date:** 2026-04-19
**Status:** Draft
**Owner:** Frank
**Scope:** Extend `/Shifts/Dashboard` with ticket-aware engagement counters, a per-department staffing table with per-period drill-down, a coordinator activity table, and a trend chart. Ship as a single PR. Include a local-only seed endpoint for manual verification.

## 1. Purpose & audience

The Volunteer Coordinator (plus Admin / NoInfoAdmin, per the existing `ShiftDashboardAccess` policy) is responsible for making sure every department's shifts get filled and that enough volunteers actually show up. The existing `/Shifts/Dashboard` surfaces urgent shifts and staffing charts, but it does not answer the coordinator's weekly questions:

- How close are we to fully staffing each period (Build / Event / Strike) across the org?
- Which ticket holders have not yet signed up — i.e., who should we reach out to?
- Are team coordinators actually logging in to approve pending signups?
- Do our engagement efforts (newsletters, announcements) actually move signups?

This dashboard extends the existing page with new panels that answer those questions directly.

## 2. Scope

**In scope (single PR):**

- New Overview panel with five counter cards
- New Departments table (all parent departments in the event, sorted by lowest % filled first) with expandable rows that unfold either subteams (when the department has subteam rotas) or per-period breakdown (when it does not)
- New Coordinator activity table (only teams with ≥1 pending signup, sorted by staleness)
- New Trends line chart (daily shift signups, daily ticket sales, daily active users) with a 7/30/90/all window toggle
- New `DevelopmentDashboardSeeder` and `POST /dev/seed/dashboard` endpoint, gated to `ASPNETCORE_ENVIRONMENT=Development` only
- Unit tests for the new service methods
- Feature-doc update in `docs/features/25-shift-management.md`
- Section invariant update in `docs/sections/Shifts.md`

**Out of scope (explicit non-goals):**

- No-show rate and bail rate (deferred — user requested for later)
- Per-event comparison / multi-event analytics
- Historical DAU beyond the most-recent login (would require an event log — out of scope)
- Hours-committed / over-committed-volunteer views
- Tag-coverage breakdown
- Configurable thresholds for stale-pending (hard-coded to 3 days)
- Per-coordinator drill-through pages
- CSV export

## 3. Architecture

Clean Architecture, same layering as the rest of the app.

### 3.1 New / modified files

| Layer | File | Change |
|---|---|---|
| Application | `src/Humans.Application/DTOs/DashboardOverview.cs` | New record |
| Application | `src/Humans.Application/DTOs/DepartmentStaffingRow.cs` | New record |
| Application | `src/Humans.Application/DTOs/CoordinatorActivityRow.cs` | New record |
| Application | `src/Humans.Application/DTOs/DashboardTrendPoint.cs` | New record |
| Application | `src/Humans.Application/Interfaces/IShiftManagementService.cs` | Add 3 methods |
| Infrastructure | `src/Humans.Infrastructure/Services/ShiftManagementService.cs` | Implement 3 methods + cache invalidation on signup mutations |
| Web | `src/Humans.Web/Models/ShiftViewModels.cs` | Extend `ShiftDashboardViewModel` |
| Web | `src/Humans.Web/Controllers/ShiftDashboardController.cs` | Call new service methods, accept `?trendWindow=` query param |
| Web | `src/Humans.Web/Views/ShiftDashboard/Index.cshtml` | Render new panels above existing content |
| Web | `src/Humans.Web/Views/ShiftDashboard/_OverviewCounters.cshtml` | New partial |
| Web | `src/Humans.Web/Views/ShiftDashboard/_DepartmentsTable.cshtml` | New partial |
| Web | `src/Humans.Web/Views/ShiftDashboard/_CoordinatorActivity.cshtml` | New partial |
| Web | `src/Humans.Web/Views/ShiftDashboard/_TrendsChart.cshtml` | New partial |
| Web | `src/Humans.Web/Controllers/DevSeedController.cs` | Add `SeedDashboard` action with stricter gate |
| Web | `src/Humans.Web/Infrastructure/DevelopmentDashboardSeeder.cs` | New seeder class |
| Tests | `tests/Humans.Application.Tests/Services/ShiftDashboardMetricsTests.cs` | New test file |
| Docs | `docs/features/25-shift-management.md` | Append "Coordinator Dashboard" section |
| Docs | `docs/sections/Shifts.md` | New invariant: dashboard methods require `ShiftDashboardAccess` policy |

### 3.2 Service contract

New methods on `IShiftManagementService`:

```csharp
Task<DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId);
Task<IReadOnlyList<CoordinatorActivityRow>> GetCoordinatorActivityAsync(Guid eventSettingsId);
Task<IReadOnlyList<DashboardTrendPoint>> GetDashboardTrendsAsync(Guid eventSettingsId, TrendWindow window);
```

New enum:

```csharp
public enum TrendWindow { Last7Days, Last30Days, Last90Days, All }
```

### 3.3 DTO shapes

```csharp
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

## 4. Metric definitions (authoritative)

These definitions are load-bearing — the unit tests pin them down and the implementation must match.

- **Event scope.** All counts and tables are scoped to a single `EventSettings` (the one the existing dashboard already resolves).
- **Shift inclusion filter.** A shift is included iff `!shift.AdminOnly` **and** `shift.Rota.IsVisibleToVolunteers`. This matches the existing browse-surface definition so the dashboard reflects what volunteers actually see.
- **Filled shift.** `COUNT(confirmed signups) >= shift.MinVolunteers`. Confirmed = `SignupStatus.Confirmed`. Pending signups are *not* counted toward filled.
- **Ticket holder.** A `User` with at least one `TicketOrder` where `PaymentStatus = Paid` and `MatchedUserId = user.Id`. Multiple orders for the same user still count as one ticket holder.
- **Engaged ticket holder.** Ticket holder with ≥1 `ShiftSignup` in the event (any status except `Cancelled`).
- **Non-ticket signup.** User with ≥1 `ShiftSignup` in the event (any status except `Cancelled`) AND zero paid `TicketOrder`s matched to them.
- **Stale pending.** `ShiftSignup` where `Status = Pending` and `CreatedAt < now - 3 days` and the shift belongs to the event.
- **Coordinator.** A `TeamMember` with `Role = Coordinator` on the team. A team may have multiple coordinators.
- **Coordinator activity row inclusion.** A team appears iff it has ≥1 pending signup on a shift in the event. Teams with zero pending are hidden (they do not need attention). Rows sort by oldest coordinator `LastLoginAt` first, breaking ties by team name.
- **Period.** Uses the existing `Shift.GetShiftPeriod(EventSettings)` function, which returns `ShiftPeriod.Build`, `.Event`, or `.Strike` based on `DayOffset` vs `GateOpeningDate`, `EventEndOffset`, `StrikeEndOffset`.
- **Department.** A *department* is a top-level team (`ParentTeamId == null`). Rotas attached directly to a department contribute to the department row. Rotas attached to a subteam (`ParentTeamId == department.Id`) also contribute to the parent department row but are additionally surfaced as subgroups.
- **Subgroup inclusion.** A department shows subgroup rows iff at least one of its subteams has ≥1 rota in the event. The subgroup list contains one row per subteam-with-rotas plus a "Direct" row for the department's own direct rotas (if any). If no subteam has rotas, `Subgroups` is empty and the UI falls back to per-period expansion.
- **Subgroup ordering.** Sort subgroups by fill % ascending; ties broken by name. The "Direct" subgroup, if present, is pinned at the top regardless of its fill %.
- **Subgroup totals.** A subgroup's `TotalShifts` / `FilledShifts` / `SlotsRemaining` / `PeriodStaffing` values sum to the parent department's aggregate (invariant asserted by tests).
- **Trend buckets.** Days are local dates in `EventSettings.TimeZoneId`. A bucket exists for every day in the window even if counts are 0.
  - **NewSignups:** count of `ShiftSignup` rows created that day for shifts in this event.
  - **NewTicketSales:** count of `TicketOrder` rows with `PurchasedAt` that day, `PaymentStatus = Paid`. Not filtered by `VendorEventId` in v1 — we treat any paid order in the window as a ticket sale signal. (Future multi-event may need to scope this.)
  - **DistinctLogins (DAU).** Count of users whose `LastLoginAt` falls on that day. **Limitation:** `User.LastLoginAt` holds only the *most recent* login per user, so buckets older than ~24h will undercount as users log in again. This is documented in the chart legend tooltip. Acceptable for v1; a proper daily-active metric would need a login-event log which is out of scope.

## 5. Caching & performance

At ~500-user scale every query is trivially fast, but the trend query GROUPs by day across months of data so it is the worst offender.

- `IMemoryCache` with 5-minute sliding TTL.
- Cache keys:
  - `dashboard-overview:{eventId}`
  - `dashboard-coordinator-activity:{eventId}`
  - `dashboard-trends:{eventId}:{window}`
- On any `ShiftSignup` create / state transition, invalidate overview + coordinator-activity keys for the event. Trend key is cheaper to leave stale — new signups only matter for today's bucket, which recomputes on the next TTL expiry.
- No concurrency tokens, no row versioning (per CLAUDE.md rules).

## 6. UI layout

Dashboard view (`/Shifts/Dashboard`) renders, top to bottom:

1. **Overview counters** — 5 cards in a responsive grid:
   - *Shifts filled* — `312 / 487 (64%)` plus three period chips (Build / Event / Strike) color-coded by threshold (≥80% green, 60–79% amber, <60% red).
   - *Ticket holders* — raw count.
   - *Ticket holders engaged* — `X / Y (Z%)` with sub-line `"N ticket holders haven't signed up"`. Visually emphasised as the primary outreach metric.
   - *Non-ticket signups* — count, sub-line `"signed up without a ticket (yet)"`.
   - *Stale pending* — count, sub-line `"pending > 3 days"`.
2. **Departments** — table sorted by lowest fill % first. Each main row shows department · total · filled · % · slots remaining. A chevron toggles an expanded area:
   - If the department has one or more subteams with ≥1 rota, the expansion shows **one row per subgroup** (the department's own direct rotas as a "Direct" row, if any, plus one row per subteam). Each subgroup row shows name · total · filled · % · slots remaining · compact Build/Event/Strike % chips.
   - If the department has no subteam rotas, the expansion shows the original three period rows (Build / Event / Strike) with their own totals/%.
   - Subgroups are sorted lowest fill % first; the "Direct" row (if present) is always pinned to the top.
   - Expand state is client-side only (no persistence).
3. **Coordinator activity** — table of teams with ≥1 pending signup. Columns: team · coordinator names · last login relative time · pending count. Logins older than 7 days render red.
4. **Trends** — Chart.js line chart with three series (new signups, new ticket sales, DAU) and a window toggle (7 / 30 / 90 / all). Default window = 30.
5. **Existing panels** — urgent shifts, staffing by day, staffing hours by priority — unchanged, below the new content.

Bootstrap 5.3 for layout, Chart.js 4.5 for the trend chart (already CDN-loaded in `_Layout.cshtml`). No new third-party dependencies.

### 6.1 Text & i18n

All user-facing text uses **"humans"** not "users"/"members" (per CLAUDE.md). New strings go into `SharedResource.resx` plus each locale file (`.es.resx`, `.de.resx`, `.fr.resx`, `.it.resx`, `.ca.resx`). "Humans" stays untranslated across all locales.

### 6.2 Nav

No new top-nav link is required — the existing "Shifts Dashboard" access path continues to serve. This satisfies the "no orphan pages" rule.

### 6.3 Authorization

Unchanged from current state: `[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]` on the controller action → Admin, NoInfoAdmin, VolunteerCoordinator only.

## 7. Seed data (`DevelopmentDashboardSeeder`)

New endpoint: `POST /dev/seed/dashboard` on `DevSeedController`.

**Gate — stricter than existing `/dev/seed/*`:**

```csharp
if (!_environment.IsDevelopment()) return NotFound();
if (!DevAuthEnabled) return NotFound();
```

Rationale: the existing endpoints run on any non-production env with DevAuth enabled (so QA + preview envs can seed budget data). The dashboard seed is explicitly local-only — `IsDevelopment()` is true only when `ASPNETCORE_ENVIRONMENT=Development`, which is the default for `dotnet run` and is not set on QA / preview / prod.

**Produced data (idempotent):**

- One `EventSettings` if none exists; otherwise use the current one. Gate opening 60 days in the future.
- 6 parent teams if missing: Gate, Infrastructure, Kitchen, Medics, Rangers, DPW.
- Subteams under Infrastructure: *Power* and *Plumbing* (exercises subteam unfolding). Other departments remain flat to exercise the per-period fallback expansion.
- 3 rotas per parent team — one per period, tagged + prioritised realistically.
- Infrastructure subteams each get 1–2 rotas of their own (in addition to Infrastructure's own direct rotas), so the parent row has a "Direct" subgroup plus two subteam subgroups.
- 8–12 shifts per rota with varied `MinVolunteers` / `MaxVolunteers` (2–8) and `Duration` (2–8h).
- ~400 `User`s with profiles:
  - ~300 with a paid `TicketOrder` matched.
  - ~30 without a ticket but with a profile (the "intend to attend" cohort).
  - ~70 with neither ticket nor signups (outreach-candidate baseline).
- Coordinators assigned — Infrastructure's two coordinators get `LastLoginAt` 9 days ago (exercises the red staleness warning); the rest within the last 48h.
- `ShiftSignup`s:
  - Gate & Kitchen land ≥85% Confirmed.
  - Strike-period rotas stay around 20% Confirmed.
  - Infrastructure/Power subteam lands ~85% Confirmed; Infrastructure/Plumbing subteam around 40% Confirmed (so subteam rows sort with Plumbing at the top of the expansion).
  - ~15 Pending signups older than 3 days (fires the stale-pending counter).
  - A few Bailed / Refused to exercise filters.
- `LastLoginAt` spread across the last 30 days for DAU shape.

**Idempotency marker:** the seeder sets `EventSettings.Name = "Seeded Nowhere 2026 (dev)"` and inspects it on re-run; if present, the operation is a no-op that returns a message.

**Teardown:** no dedicated endpoint. Local devs drop and recreate the database when they want a clean slate.

## 8. Testing

### 8.1 Unit tests (`ShiftDashboardMetricsTests`)

EF in-memory DB, same shape as the existing `ShiftUrgencyTests`. Cover:

- Counter totals including edge cases: zero shifts, zero signups, zero ticket holders
- Filled-shift threshold: `MinVolunteers - 1` confirmed → not filled; `MinVolunteers` confirmed → filled; `MinVolunteers + 1` confirmed → still filled
- Period breakdown: shifts split correctly across Build / Event / Strike using `DayOffset` boundaries from `EventSettings`
- Ticket-holder classification: user with paid order = ticket holder; user with only refunded order = not; user with multiple paid orders counts once
- Engaged-ticket-holder: ticket holder with `Cancelled`-only signups is *not* engaged
- Non-ticket-signup: user with signups but no paid order counted; user with signups and a paid order not counted
- Stale-pending: signup created exactly 3 days ago → not stale; 3 days + 1 minute → stale; status = Confirmed → never stale regardless of age
- Coordinator activity: team with zero pending signups excluded; team with multiple coordinators lists all; row ordering by oldest login first
- Trend bucketing: zero-count days present in the range; window boundaries inclusive on start, exclusive on end+1; trend respects `EventSettings.TimeZoneId`
- TrendWindow.All uses event creation date as the start
- Subgroup computation: department with zero subteam rotas yields empty `Subgroups`; department with subteam rotas + direct rotas yields one "Direct" row plus one row per subteam with rotas; subteam with no rotas is omitted
- Subgroup aggregation invariant: sum of subgroup `TotalShifts`, `FilledShifts`, `SlotsRemaining`, and per-period counters equals the parent department's totals
- Subgroup ordering: "Direct" pinned top; remaining rows sorted by fill % ascending with name tiebreak

### 8.2 Manual test plan (included in PR description)

1. `dotnet run --project src/Humans.Web` against a clean dev DB.
2. Log in as Admin via dev login.
3. `curl -X POST http://localhost:5xxx/dev/seed/dashboard` (with antiforgery) or click the seed button.
4. Visit `/Shifts/Dashboard`:
   - Verify 5 counter cards render with non-zero values matching seeder expectations.
   - Verify Infrastructure's strike column is the lowest % (designed-in).
   - Click Infrastructure row — subgroup rows appear: a "Direct" row plus Power and Plumbing subteam rows, with Plumbing at the top (lowest fill %).
   - Click Gate row (no subteams) — three period rows appear (Build / Event / Strike), confirming the fallback expansion.
   - Verify Coordinator activity shows Infrastructure with red "9 days ago".
   - Toggle trend window 7 / 30 / 90 / all — chart updates without errors.
5. Log out, log in as a plain volunteer — verify `/Shifts/Dashboard` returns 403 / NotFound.
6. Rebuild with `ASPNETCORE_ENVIRONMENT=Production` locally and verify `POST /dev/seed/dashboard` returns 404.
7. Screenshots of final state attached to PR.

### 8.3 Automated verification before commit

- `dotnet build Humans.slnx` passes with no new warnings.
- `dotnet test Humans.slnx` passes.
- No new EF migration files appear in the diff (sanity check).

## 9. Risks & open questions

- **DAU accuracy.** `LastLoginAt` only captures the most recent login, so historical days undercount. Documented in the chart tooltip; acceptable for v1.
- **Ticket order event scoping.** `TicketOrder.VendorEventId` exists but is not enforced in v1 — we treat any paid order as a ticket. If the nonprofit starts selling tickets for multiple events concurrently, the "ticket holders" count will conflate them. Out of scope; flagged for future work.
- **Performance edge case.** A ~10,000-signup event with a 90-day trend window would GROUP BY up to 90 buckets across ~10k rows. Still sub-second in Postgres at this scale, but if the org ever grows past current expectations, consider materialising a daily-stats table.
- **Team hierarchy.** Dashboard rows are parent departments. Subteams with rotas surface inside the expansion of their parent's row (see §4 "Subgroup inclusion"). Only one level of hierarchy exists in the data model (`Team.ParentTeamId` is at most one hop deep), so the expansion is flat — no recursive nesting.

## 10. Git plan

- Branch: `feat/volunteer-coordinator-dashboard` off `origin/main` (peter's fork).
- Single PR to peter's `main`. Coolify opens a preview env at `{pr_id}.n.burn.camp` with dev login enabled.
- PR description links this spec.
- Squash merge on approval.
- Production promotion handled later as a batched rebase-merge to `upstream/main`.

## 11. Follow-ups (not this PR)

- No-show rate + bail rate counters
- Per-coordinator drill-through page
- Cross-event comparison
- Tag-coverage panel
- CSV export of the outreach-candidate list (ticket holders with no signup)
