# Shift Management v2 — Design Specification

**March 17, 2026 | v1.0**

Based on volunteer coordinator feedback and designer Figma mockup review.

---

## 1. Overview

Shift management v1 (Slices 1–3) shipped March 17, 2026. The volunteer coordinator reviewed the system against VIM (the previous tool) and identified a critical gap: build/strike periods work fundamentally differently from event-time shifts. VIM had a "Projects" concept for multi-day build/strike commitments that was explicitly deferred during the v1 design. This spec addresses that gap and several other refinements.

### Source Documents

- `docs/specs/2026-03-17-shift-v2-changes.md` — consolidated change list
- `docs/specs/2026-03-16-shift-management-design.md` — v1 design spec
- `docs/specs/volunteer-managment-proposal-v1.md` — original volunteer proposal
- `docs/specs/view-components.md` — UI component specs from designer review

### Key Design Decisions

| Decision | Resolution | Rationale |
|----------|-----------|-----------|
| Build/strike signup model | **Option A: one all-day shift per day, bulk signup** | Keeps existing data model (one ShiftSignup per Shift). Volunteers sign up for a date range; system creates per-day signups behind the scenes. |
| Shift title | **Removed entirely** | Rota name + time slot (or date for all-day) is sufficient. Frank and Daniela both agreed title is unnecessary. |
| Practical info location | **On Rota only** | All shifts in a rota share the same instructions. Different instructions = different rota. |
| Rota period | **Explicit enum on Rota** | Drives creation UX (all-day vs time-slotted) and signup UX (date-range vs individual). Distinct from computed ShiftPeriod. |
| Deactivation | **Removed — delete only** | Deactivation solved a problem that doesn't exist at this scale. Delete is blocked if confirmed signups exist. |
| NoInfoAdmin permissions | **No change** | Can view, voluntell, approve/refuse. Cannot create/edit rotas or shifts (except own team). Coordinator feedback confirmed this is correct. |
| General volunteer pool | **In scope, date-based** | Volunteers mark which days they're available. Coordinators see pool when voluntelling. |
| Role period tags | **Single-select on TeamRoleDefinition** | YearRound/Build/Event/Strike. Enables roster page filtering. |
| Shift title replacement | **Rota name used everywhere** | `shift.Rota.Name` replaces `shift.Title` in all views, exports, iCal, emails. |
| Event rota bulk generation | **Separate "Generate Shifts" action** | Keeps rota creation simple. Bulk generation as a post-creation action. Manual "Add Shift" still works. |

---

## 2. Data Model Changes

### 2.1 Shift Entity (modified)

| Change | Field | Type | Notes |
|--------|-------|------|-------|
| **Remove** | `Title` | — | Shifts identified by `Rota.Name` + time/date |
| **Remove** | `IsActive` | — | Delete replaces deactivate |
| **Add** | `IsAllDay` | bool (default false) | All-day shifts store StartTime=00:00, Duration=24h but UI ignores these |

All other fields unchanged: `Id`, `RotaId`, `Description`, `DayOffset`, `StartTime`, `Duration`, `MinVolunteers`, `MaxVolunteers`, `AdminOnly`, `CreatedAt`, `UpdatedAt`.

### 2.2 Rota Entity (modified)

| Change | Field | Type | Notes |
|--------|-------|------|-------|
| **Remove** | `IsActive` | — | Delete replaces deactivate |
| **Add** | `Period` | RotaPeriod (string) | Build / Event / Strike |
| **Add** | `PracticalInfo` | string? (max 2000) | Meeting point, pre-shift instructions, what to bring |

All other fields unchanged: `Id`, `EventSettingsId`, `TeamId`, `Name`, `Description`, `Priority`, `Policy`, `CreatedAt`, `UpdatedAt`.

### 2.3 New Entity: GeneralAvailability

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `UserId` | Guid | FK → User. One record per user per event. |
| `EventSettingsId` | Guid | FK → EventSettings |
| `AvailableDayOffsets` | jsonb (List\<int\>) | Day offsets the volunteer is available |
| `CreatedAt` | Instant | |
| `UpdatedAt` | Instant | |

Unique constraint on `(UserId, EventSettingsId)`.

**Navigation properties:** `User`, `EventSettings`. Do NOT add `ICollection<GeneralAvailability>` to `User` or `EventSettings` — avoid loading this data in unrelated queries.

**EF configuration:** New `GeneralAvailabilityConfiguration.cs`. `AvailableDayOffsets` requires `HasColumnType("jsonb")` with a `ValueComparer<List<int>>` for change tracking (same pattern as `EventSettings.EarlyEntryCapacity`). FK delete behavior: RESTRICT for both User and EventSettings.

### 2.4 TeamRoleDefinition (modified)

| Change | Field | Type | Notes |
|--------|-------|------|-------|
| **Add** | `Period` | RolePeriod (string, default YearRound) | YearRound / Build / Event / Strike |

### 2.5 New Enums

```
RotaPeriod:  Build(0), Event(1), Strike(2)
RolePeriod:  YearRound(0), Build(1), Event(2), Strike(3)
```

Both stored as string via `HasConversion<string>()`. Query filtering must use `Contains()` over explicit value lists, not inequality operators (see `CODING_RULES.md` — string-stored enums break with `>`, `>=`, `<`, `<=`).

New enum files: `src/Humans.Domain/Enums/RotaPeriod.cs` and `src/Humans.Domain/Enums/RolePeriod.cs`.

`RotaPeriod` is distinct from the existing `ShiftPeriod` enum (which is computed from DayOffset and never stored). `RotaPeriod` is explicitly set by the coordinator and drives creation/signup UX.

### 2.6 Removed: Signup Garbage Collection

The `SignupGarbageCollectionJob` (which cancelled signups on deactivated shifts after 7 days) is removed since deactivation no longer exists.

---

## 3. UX Flows

### 3.1 Build/Strike Rota Creation

**Step 1 — Create rota:**

On `/Teams/{slug}/Shifts`, coordinator fills in:
- Name (required)
- Period: Build / Event / Strike (dropdown, required)
- Priority: Normal / Important / Essential
- Policy: Public / Require Approval
- Description (optional)
- Practical Info (optional)

**Step 2 — Configure staffing (Build/Strike only):**

If Period = Build or Strike, a per-day staffing grid appears after creation:
- One row per day in the period range (e.g., Build: BuildStartOffset to day -1)
- Each row shows: resolved date + day offset (e.g., "Wed Jul 2 — Day -5"), Min input, Max input
- Defaults: Min=2, Max=5
- Submit generates one all-day shift per day with the specified Min/Max

**If Period = Event:** No auto-generation. Rota created empty. Coordinator adds shifts manually or via bulk generation.

### 3.2 Event Rota Bulk Shift Generation

On an existing event-period rota, coordinator clicks "Generate Shifts":

1. Start Day dropdown (days in event range)
2. End Day dropdown
3. Time slot rows (add/remove dynamically):
   - Start Time (time input)
   - Duration in hours (number input)
4. Min/Max volunteers (shared across all generated shifts, defaults: 2/5)
5. Submit → Cartesian product: every day × every time slot = one shift

Example: Start=Day 0, End=Day +3, slots=[08:00/4h, 14:00/4h] → 8 shifts created.

Coordinator can still manually add/edit/delete individual shifts after generation.

### 3.3 Build/Strike Volunteer Signup

**Browsing (`/Shifts`):**
- Build/strike rotas displayed as cards showing: rota name, team name, date range, per-day fill rates
- Volunteer clicks a rota → sees date range with per-day slot availability

**Signing up:**
1. Volunteer picks start date and end date from dropdowns (constrained to rota's day range, only days with available slots shown)
2. Submit → system creates one `ShiftSignup` per all-day shift in that range
3. Overlap check runs against all selected days. If any day conflicts, the whole signup is blocked with a message showing which day(s) conflict.
4. For RequireApproval rotas, all signups created as Pending
5. For Public rotas, all signups auto-confirmed

**Viewing on `/Shifts/Mine`:**
- Build/strike signups grouped by rota, displayed as a date range (e.g., "Construction Crew — Jul 2–Jul 7")
- Single "Bail" button for the entire range

**Bailing:**
- Bail applies to all signups sharing the same `SignupBlockId` (see §2.1a below)
- No partial bail — to shorten a commitment, bail and re-signup for the shorter range
- EE freeze rules still apply (build-period bail blocked after EarlyEntryClose for non-privileged users)

### 2.1a ShiftSignup Entity (modified)

| Change | Field | Type | Notes |
|--------|-------|------|-------|
| **Add** | `SignupBlockId` | Guid? | Shared value for all signups created by a single `SignUpRangeAsync` call. Null for individual event-time signups. Used by `BailRangeAsync` to identify the block. |

This avoids ambiguity when grouping signups for bail — the block ID is deterministic and set at signup time, not inferred from consecutive day offsets.

### 3.4 General Volunteer Pool

**Volunteer registers availability:**
1. Section on `/Shifts` (below shift browsing) or on `/Shifts/Mine`: "General Availability"
2. Date grid showing all days in the event period (build through strike)
3. Volunteer checks off available days
4. Submit → creates/updates `GeneralAvailability` record

**Coordinator sees pool:**
- Voluntell search results (on ShiftAdmin and ShiftDashboard) include a "Pool" badge for volunteers who marked themselves as generally available for the shift's day
- No separate pool management page — the pool enriches the existing voluntell workflow

### 3.5 Delete Replaces Deactivate

**Rotas:**
- "Deactivate" button → "Delete" button with confirmation dialog
- Delete blocked if any child shift has confirmed signups (error: "Cannot delete — N humans have confirmed signups. Bail or reassign them first.")
- If only pending signups exist on child shifts, they are cancelled on delete
- Cascade: deleting a rota deletes all its child shifts (which cancel their pending signups)

**Shifts:**
- Same pattern: delete blocked if confirmed signups exist, pending signups cancelled

### 3.6 Role Period Filter

On `/Teams/{slug}/Roster`:
- New dropdown filter: "All Periods" / "Year-round" / "Build" / "Event" / "Strike"
- Filters the role list to show only roles matching the selected period
- Default: "All Periods"

---

## 4. Display Rules

### 4.1 Dates vs Offsets

| Context | Display |
|---------|---------|
| Volunteer-facing views (`/Shifts`, `/Shifts/Mine`) | Resolved dates only (e.g., "Wed Jul 2") |
| Coordinator views (`/Teams/{slug}/Shifts`, `/Shifts/Dashboard`) | Both (e.g., "Day -5 — Wed Jul 2") |
| Staffing grid (rota creation) | Both |
| iCal feed | Resolved dates only |
| Email notifications | Resolved dates only |
| CSV exports | Resolved dates only |

### 4.2 Shift Identification (Title Removal)

With `Title` removed, shifts are identified everywhere as:

| Shift Type | Display Format |
|------------|---------------|
| Event-time | "{Rota.Name} — {StartTime}–{EndTime}" (e.g., "Gate Shifts — 08:00–12:00") |
| All-day | "{Rota.Name} — {Date}" (e.g., "Construction Crew — Wed Jul 2") |

This applies to: browse page, my shifts, admin views, iCal events, email notifications, CSV exports, urgency dashboard.

---

## 5. Navigation & Routing

### 5.1 Modified Routes

| Route | Change |
|-------|--------|
| `/Teams/{slug}/Shifts` | Period dropdown on Create Rota. Staffing grid for build/strike. Generate Shifts for event rotas. Delete replaces Deactivate. |
| `/Shifts` | Build/strike rotas show date-range signup. General Availability section. |
| `/Shifts/Mine` | Build/strike signups displayed as date ranges. Range bail. |
| `/Shifts/Dashboard` | Voluntell search enriched with pool indicator. |
| `/Teams/{slug}/Roster` | Period filter dropdown on role list. |

### 5.2 No New Routes

All functionality fits into existing pages. No new controllers needed. All functionality reachable from existing navigation:

- Volunteer: **Shifts** nav → browse → sign up → My Shifts to view/bail
- Volunteer: **Shifts** nav → General Availability section → mark days
- Coordinator: **Team page** → Shifts → create rota → configure staffing / generate shifts
- Coordinator: **Team page** → Roster → filter by period

---

## 6. Migration Strategy

### 6.1 Database Migration

**Single migration** (acceptable at this scale with QA gating):

Drops:
- `shifts.title`
- `shifts.is_active`
- `rotas.is_active`

Adds:
- `shifts.is_all_day` (bool, default false)
- `shift_signups.signup_block_id` (Guid?, nullable)
- `rotas.period` (string, max 50, default 'Event')
- `rotas.practical_info` (string?, max 2000, nullable)
- `team_role_definitions.period` (string, max 50, default 'YearRound')
- New table `general_availability`

**Pre-migration cleanup (run in migration `Up()` before column drops):**
1. Delete shifts where `IsActive=false` AND no confirmed signups exist (cancel any pending signups first)
2. Set `IsActive=true` on remaining shifts where `IsActive=false` AND confirmed signups exist
3. Delete rotas where `IsActive=false` AND no child shifts remain after step 1
4. Set `IsActive=true` on remaining rotas where `IsActive=false` AND child shifts remain
5. Drop the `is_active` columns from both tables

### 6.2 Code Impact

| Area | Changes |
|------|---------|
| **Shift entity** | Remove `Title`, `IsActive`. Add `IsAllDay`. |
| **ShiftSignup entity** | Add `SignupBlockId` (Guid?, nullable). |
| **Rota entity** | Remove `IsActive`. Add `Period`, `PracticalInfo`. |
| **TeamRoleDefinition** | Add `Period`. |
| **New enums** | Create `RotaPeriod.cs` and `RolePeriod.cs` in `src/Humans.Domain/Enums/`. |
| **New entity** | Create `GeneralAvailability.cs` in `src/Humans.Domain/Entities/`. |
| **EF configurations** | Update `ShiftConfiguration` (drop Title/IsActive, add IsAllDay). Update `RotaConfiguration` (drop IsActive, add Period/PracticalInfo). Update `ShiftSignupConfiguration` (add SignupBlockId). New `GeneralAvailabilityConfiguration.cs` with jsonb column type and `ValueComparer<List<int>>`. |
| **IShiftManagementService** | New: `CreateBuildStrikeShiftsAsync` (bulk day generation), `GenerateEventShiftsAsync` (Cartesian product). Remove: `DeactivateRotaAsync`, `DeactivateShiftAsync`. Update: remove `IsActive` filters from all queries including `GetShiftsSummaryAsync`, `GetDepartmentsWithRotasAsync`, `GetBrowseShiftsAsync`, `GetUrgentShiftsAsync`, `GetStaffingDataAsync`. Add: `DeleteRotaAsync`/`DeleteShiftAsync` with confirmed-signup guards. |
| **IShiftSignupService** | New: `SignUpRangeAsync` (multi-day, sets shared `SignupBlockId`), `BailRangeAsync` (bail all signups with matching `SignupBlockId`). |
| **ShiftAdminController** | Period on create rota. Staffing grid POST. Generate shifts POST. Delete replaces deactivate. |
| **ShiftsController** | Date-range signup for build/strike. General availability CRUD. |
| **ShiftDashboardController** | Pool indicator in voluntell search results. |
| **View models** | Remove `Title` from `CreateShiftModel`. Remove `IsActive` from `EditShiftModel` and `EditRotaModel`. Add `Period` and `PracticalInfo` to `CreateRotaModel`/`EditRotaModel`. New: `StaffingGridModel`, `GenerateEventShiftsModel`, `GeneralAvailabilityViewModel`. Update `NoShowHistoryItem.ShiftTitle` → populated from `shift.Rota.Name`. |
| **Views** | All `shift.Title` → `shift.Rota.Name` (including urgency dashboard references via `UrgentShift.Shift`). Period-aware forms. Build/strike browse as date ranges. Delete buttons replace deactivate. General availability section. |
| **Hangfire jobs** | Delete `SignupGarbageCollectionJob.cs` file and remove its Hangfire registration. |
| **iCal feed** | Event title: `"{Rota.Name} — {Department}"` instead of `"{Title} — {Department}"`. |
| **Email templates** | Same title change. |

### 6.3 Test Updates

| Test Area | Changes |
|-----------|---------|
| `ShiftTests` | Remove title-related assertions. Add `IsAllDay` tests (all-day shift resolution, display format). |
| `ShiftSignupTests` | No changes (state machine unchanged). |
| `ShiftSignupServiceTests` | New: `SignUpRange` tests (multi-day, overlap across range, partial availability). New: `BailRange` tests. |
| `ShiftManagementServiceTests` | New: bulk shift generation for build/strike. New: Cartesian product generation for event rotas. New: delete with confirmed-signup guard. Remove: deactivation tests. |
| `ShiftUrgencyTests` | Update title references to rota name. |
| **New test file** | `GeneralAvailabilityTests` — CRUD, per-event uniqueness, pool query integration. |

---

## 7. Implementation Slices

### Slice A — Schema + Title Removal (foundation, everything depends on this)

- Migration: pre-migration cleanup of IsActive=false records, add new columns/table, drop Title and IsActive
- Create `RotaPeriod.cs`, `RolePeriod.cs` enums and `GeneralAvailability.cs` entity
- Update Shift entity (remove Title/IsActive, add IsAllDay)
- Update ShiftSignup entity (add SignupBlockId)
- Update Rota entity (remove IsActive, add Period/PracticalInfo)
- Update TeamRoleDefinition (add Period)
- Update EF configurations (ShiftConfiguration, RotaConfiguration, ShiftSignupConfiguration, new GeneralAvailabilityConfiguration)
- Update all views, view models, iCal, emails to use `Rota.Name` instead of `Title`
- Update `NoShowHistoryItem.ShiftTitle` to use rota name
- Remove deactivation code (DeactivateRotaAsync, DeactivateShiftAsync), add delete with confirmed-signup guards
- Delete `SignupGarbageCollectionJob.cs` and remove its Hangfire registration
- Remove `IsActive` filters from all service queries
- Update all existing tests

### Slice B — Build/Strike Shifts (the primary gap)

- `RotaPeriod` enum and period-aware rota creation form
- Per-day staffing grid UI for build/strike rota creation
- Bulk all-day shift generation (`CreateBuildStrikeShiftsAsync`)
- Date-range signup flow (`SignUpRangeAsync`)
- Range bail flow (`BailRangeAsync`)
- Build/strike display as date ranges on `/Shifts` and `/Shifts/Mine`
- Tests for all new flows

### Slice C — Event Rota Bulk Generation

- "Generate Shifts" action on event-period rotas
- Cartesian product generation (`GenerateEventShiftsAsync`)
- UI: start day, end day, time slot templates
- Tests for generation

### Slice D — General Volunteer Pool

- `GeneralAvailability` entity and service
- Availability date grid UI on `/Shifts` or `/Shifts/Mine`
- Pool indicator in voluntell search results
- Tests

### Slice E — Role Period Tags + Practical Info

- `RolePeriod` enum on `TeamRoleDefinition`
- Period filter on roster page
- `PracticalInfo` field on Rota (form field + display on shift browse/signup)
- Tests

### Slice Dependencies

```
Slice A ← Slice B (needs schema changes)
Slice A ← Slice C (needs schema changes)
Slice A ← Slice D (needs schema changes)
Slice A ← Slice E (needs schema changes)
Slices B, C, D, E are independent of each other
```

### Priority Order

1. **Slice A** — unblocks everything
2. **Slice B** — the critical gap Frank identified, needed for ticketing
3. **Slice C** — convenience for coordinators populating event shifts
4. **Slice E** — practical info + role periods (quick wins)
5. **Slice D** — general pool (nice-to-have, least urgent)

---

## 8. Open Items

| Item | Status |
|------|--------|
| Public-facing shift/department info (pre-registration visibility) | Deferred — separate spec |
| Gate printing of volunteer schedules | Deferred — Slice 4 exports cover this |
| EE gaming detection (utilization %) | Deferred — placeholder in v1 spec still applies |
| On-site presence tracking ("On Site" / "Expected" statuses) | Deferred — designer concept, not coordinator-requested |
| Effort/points system for fair distribution | Deferred — Frank mentioned but acknowledged it's future |
