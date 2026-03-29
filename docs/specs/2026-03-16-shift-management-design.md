# Shift Management for Humans — Design Specification

**Elsewhere 2026 | v1.2 | March 16, 2026**

---

## 1. Overview

Build volunteer shift management natively into Humans, replacing VIM (FIST). The full volunteer journey — registration → shift signup → on-site coordination — lives in one system. Scoped as an MVP for Elsewhere 2026, built in vertical slices.

### Source Documents

- `docs/specs/vim-shift-management-design.md` — technical extraction of VIM capabilities
- `docs/specs/volunteer-managment-proposal-v1.md` — volunteer-authored requirements

### Key Design Decisions

| Decision | Resolution | Rationale |
|----------|-----------|-----------|
| Department entity | **Reuse existing parent teams** | Humans already has `Team.ParentTeamId` hierarchy displayed as "Departments" in the UI. No new entity. |
| Projects (multi-day duties) | **Deferred** — shifts only for MVP | Model project-style work as multiple shifts in a rota. Add a Project entity later if needed. |
| Shift date storage | **Relative offsets** — DayOffset + StartTime + Duration | All dates relative to gate opening. Change `GateOpeningDate` and the entire schedule shifts. Year-to-year reuse with no migration. |
| MetaLead role | **Department Coordinator** | A Coordinator of a parent team (department). Existing `TeamRoleDefinition.IsManagement` system. No new governance role. |
| NoInfo role | **`NoInfoAdmin` governance role** | New entry in `RoleNames`, alongside `TeamsAdmin`, `CampAdmin`, `TicketAdmin`. Cross-team operational access. |
| Shift ownership | **Departments only** | Rotas/shifts belong to parent teams (departments), not sub-teams. Simplifies authorization. |
| Signup approval | **Per Rota** | The Rota sets Public or RequireApproval. All child shifts inherit. |
| Overlap handling | **Block with error** | Deliberate simplification over the volunteer proposal's swap flow. Two-step (bail then re-signup) avoids atomicity edge cases. Revisit if user feedback shows friction. |
| Capacity enforcement | **Soft warning** — no DB transactions | Capacity and EE caps are app-side warnings, not hard blocks. No transactional enforcement. |
| Early Entry cap | **Per-day step function**, shared with barrios | `Dictionary<int, int>` mapping day offset → max people on site. Barrios allocation subtracted from available shift-EE slots. |
| Event scoping | **One active event** at a time | EventSettings is effectively a singleton. |
| System open gate | **Manual toggle** | `IsShiftBrowsingOpen` boolean on EventSettings, flipped by Admin. |
| Profile extensions | **Event-scoped volunteer data** | Skills, quirks, dietary, etc. collected via a separate "Event Volunteer" card. Not shown to all humans — only those participating in shifts. Storage mechanism deferred to implementation. |
| Medical conditions | **Dedicated field on volunteer event profile** | Separate from `BoardNotes`. Visible to owner + NoInfoAdmin + Admin (not Board — NoInfo needs it for on-site safety, Board doesn't). |
| JSON export/import | **Not needed** | Relative offsets enable year-to-year reuse without export tooling. |
| URLs | **PascalCase** | Matches existing codebase convention (`/Teams`, `/Admin`, `/Board`). |
| UI terminology | **"humans" not "volunteers"** | Per CLAUDE.md, all user-facing text uses "humans." Code/entity names (e.g., `MinVolunteers`, `GlobalVolunteerCap`) are unaffected — only UI labels, headings, and localization strings. |

---

## 2. Data Model

### 2.1 EventSettings (singleton)

| Field | Type | Purpose |
|-------|------|---------|
| Id | Guid | PK |
| EventName | string | e.g., "Elsewhere 2026" |
| TimeZoneId | string | IANA timezone, e.g., "Europe/Madrid" |
| GateOpeningDate | LocalDate | Reference date (day 0) for all shift offsets |
| BuildStartOffset | int | Day offset when build period starts (e.g., -30) |
| EventEndOffset | int | Day offset of last event day (e.g., +3 for a 4-day event, days 0–3). Defines boundary between Event and Strike periods. |
| StrikeEndOffset | int | Day offset when strike period ends (e.g., +7) |
| EarlyEntryCapacity | jsonb | `Dictionary<int, int>` — day offset → max people on site. Step function: set level holds until next defined level. Days before the first defined key have capacity 0. |
| BarriosEarlyEntryAllocation | jsonb? | `Dictionary<int, int>?` — day offset → barrios EE headcount. Admin-entered. Subtracted from EarlyEntryCapacity to get available shift-EE slots. |
| EarlyEntryClose | Instant | Cutoff after which only Dept Coordinators/NoInfoAdmin/Admin can bail build-period signups |
| IsShiftBrowsingOpen | bool | Manual toggle for volunteer access to shift browsing/signup |
| GlobalVolunteerCap | int? | Soft cap — warning on unique volunteers with any confirmed signup (all periods, not just event-time) |
| ReminderLeadTimeHours | int | Hours before shift start to send reminder email (e.g., 48) |
| IsActive | bool | Marks the current/active event. False for archived past events. Only one EventSettings should have IsActive=true at a time. |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

### 2.2 Rota (shift container, belongs to department)

| Field | Type | Purpose |
|-------|------|---------|
| Id | Guid | PK |
| EventSettingsId | Guid | FK to EventSettings — scopes this rota to a specific event year |
| TeamId | Guid | FK to Team — **must be a parent team (department), not a system team** |
| Name | string | e.g., "Bar Morning Shifts" |
| Description | string? | |
| Priority | DutyPriority | Normal / Important / Essential |
| Policy | SignupPolicy | Public / RequireApproval |
| IsActive | bool | Soft-delete (default true) |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Navigation properties:** `EventSettings`, `Team`, `Shifts` (collection).

**Validation:** `TeamId` must reference a team where `ParentTeamId IS NULL` AND `SystemTeamType = None` (a department, not a sub-team or system team like Volunteers/Board). Enforced at the application service layer (consistent with existing `TeamService` patterns).

### 2.3 Shift (belongs to Rota)

| Field | Type | Purpose |
|-------|------|---------|
| Id | Guid | PK |
| RotaId | Guid | FK to Rota |
| Title | string | e.g., "Morning Bar Shift" |
| Description | string? | |
| DayOffset | int | Relative to `EventSettings.GateOpeningDate` |
| StartTime | LocalTime | In event timezone |
| Duration | Duration | How long the shift lasts (NodaTime `Duration`). Store as `bigint` (total seconds) with a custom `ValueConverter<Duration, long>`, since `Npgsql.NodaTime` does not provide a built-in converter for `Duration`. |
| MinVolunteers | int | Minimum needed (urgency indicator when below) |
| MaxVolunteers | int | Capacity — soft warning when reached |
| AdminOnly | bool | Hidden from regular volunteers |
| IsActive | bool | Soft-delete (default true) |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Computed properties (not stored):**
- `EndTime` — computed from StartTime + Duration (handles overnight naturally)
- `IsEarlyEntry` — `DayOffset < 0`
- `AbsoluteStart` — resolved from EventSettings: `GateOpeningDate.PlusDays(DayOffset).At(StartTime).InZoneLeniently(tz).ToInstant()` (lenient to handle DST transitions — picks earlier mapping for ambiguous times, instant after gap for skipped times)
- `AbsoluteEnd` — `AbsoluteStart + Duration`
- `ShiftPeriod` — computed from DayOffset: `< 0` = Build, `0..EventEndOffset` = Event, `> EventEndOffset` = Strike

**Navigation properties:** `Rota`, `DutySignups` (collection).

**Validation:** `DayOffset` must be within `BuildStartOffset..StrikeEndOffset`.

### 2.4 DutySignup (links User to Shift)

| Field | Type | Purpose |
|-------|------|---------|
| Id | Guid | PK |
| UserId | Guid | FK to User |
| ShiftId | Guid | FK to Shift |
| Status | SignupStatus | Pending / Confirmed / Refused / Bailed / Cancelled / NoShow |
| Enrolled | bool | True if voluntold (lead-assigned, not self-signup) |
| EnrolledByUserId | Guid? | Who voluntold them (only when Enrolled=true) |
| ReviewedByUserId | Guid? | Who approved/refused/marked no-show |
| ReviewedAt | Instant? | When approved/refused/marked no-show |
| StatusReason | string? | Free-text reason for bail/cancel/refuse (shown in iCal descriptions) |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Navigation properties:** `User`, `Shift`, `EnrolledByUser`, `ReviewedByUser`.

**`Cancelled` status:** System-only — produced by the GC job (deactivated shifts) and hard-delete cascade (Pending signups). Leads/admins who want to remove a volunteer use Bail (which records the actor in `ReviewedByUserId`). `Cancelled` signups may have null `ReviewedByUserId`.

**GDPR account deletion:** When a user's account is deleted via `ProcessAccountDeletionsJob`, active signups (Confirmed/Pending) are set to `Cancelled`. Historical signups (Bailed, NoShow, Refused, Cancelled) retain their `UserId` FK — the FK points to the now-anonymized User record (the existing deletion job already anonymizes the User entity to "Deleted Human"). This preserves aggregate reporting and FK integrity while complying with GDPR. The `ICalToken` and volunteer event profile data are also deleted.

### 2.5 Enums

```
DutyPriority:   Normal(0), Important(1), Essential(2)
SignupPolicy:   Public(0), RequireApproval(1)
SignupStatus:   Pending(0), Confirmed(1), Refused(2), Bailed(3), Cancelled(4), NoShow(5)
```

All stored as string via `HasConversion<string>()`.

### 2.6 EmailOutboxMessage Change

Add `DutySignupId Guid?` FK — used for linking notifications to signups. Deduplication key is `(DutySignupId, TemplateName)` — one email of each template type per signup. The existing `TemplateName` field on `EmailOutboxMessage` combined with the new FK provides sufficient dedup granularity.

### 2.7 New RoleNames Constant

`NoInfoAdmin` — governance role granting cross-team operational access for on-site coordination (urgency dashboard, voluntell, any-team signup management).

### 2.8 iCal Token on User

Add `ICalToken Guid?` to the `User` entity. A standard Guid generated on first access to `/Shifts/Mine`. Used as the path parameter in `/ICal/{token}.ics`. User can regenerate from `/Shifts/Mine`, which replaces the Guid and invalidates old subscriptions.

### 2.9 Volunteer Event Profile

Event-scoped volunteer data, collected via a separate "Event Volunteer" card on the profile edit page. Only visible when `IsShiftBrowsingOpen = true` or the user has existing signups.

**Data collected:**

| Category | Type | Values |
|----------|------|--------|
| Skills | Multi-select | Bartending, First Aid, Driving, Sound, Electrical, Construction, Cooking, Art, DJ, Other |
| Quirks | Multi-select | Sober Shift, Work In Shade, Night Owl, Early Bird, Quiet Work, Physical Work OK, No Heights |
| Languages | Multi-select | English, Spanish, French, German, Italian, Portuguese, Other |
| Dietary | Single-select | Omnivore, Vegetarian, Vegan, Pescatarian |
| Allergies | Multi-select | Celiac, Shellfish, Nuts, Tree Nuts, Soy, Egg |
| Intolerances | Multi-select | Gluten, Peppers, Shellfish, Nuts, Egg, Lactose, Other |
| Medical Conditions | Free text, optional | Health/safety info for on-site coordination. **Restricted visibility.** |

**Storage mechanism:** Deferred to implementation. Options: individual jsonb columns (like CampSeason.Vibes), single jsonb blob, or separate entity. At ~500 users all approaches work. Decision based on query patterns.

**Visibility (general fields):** Profile owner, Department Coordinators (for their dept's signups), NoInfoAdmin, Admin.

**Visibility (Medical Conditions):** Profile owner, NoInfoAdmin (for on-site safety decisions), Admin only. NOT visible to Department Coordinators or Board unless they also hold NoInfoAdmin.

**Important:** The volunteer event profile visibility model is different from the existing `ContactFieldVisibility` system used by Profile/ContactField. It must NOT be mixed into the existing `ProfileService.GetProfileAsync()` pipeline — it needs its own read path or service method.

**Notification preferences (stored alongside event profile data):**
- `SuppressScheduleChangeEmails` (bool, default false) — opt out of schedule change email notifications

**Managed value lists:** Enforced at application layer. New values can be added without migration.

---

## 3. Shift Date/Time System

### 3.1 Relative Offsets

All shift scheduling is relative to `EventSettings.GateOpeningDate` (day 0). Shifts store `DayOffset` (int) + `StartTime` (LocalTime) + `Duration` (NodaTime `Duration` — a pure time span).

**Resolving to absolute times:**
```csharp
var tz = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];
var date = eventSettings.GateOpeningDate.PlusDays(shift.DayOffset);
var startInstant = date.At(shift.StartTime).InZoneLeniently(tz).ToInstant();
var endInstant = startInstant.Plus(shift.Duration);
```

**Overnight shifts:** Handled naturally by Duration. A shift starting at 22:00 with `Duration.FromHours(8)` ends at 06:00 the next day — no special case logic.

**Year-to-year reuse:** Change `GateOpeningDate` from `2026-07-07` to `2027-07-06`. Every shift's absolute dates shift automatically. No migration.

### 3.2 Period Classification

Computed from DayOffset using EventSettings boundaries, not stored:
- `DayOffset < 0` → **Build** period
- `0 <= DayOffset <= EventEndOffset` → **Event** period
- `DayOffset > EventEndOffset` → **Strike** period

`BuildStartOffset`, `EventEndOffset`, and `StrikeEndOffset` on EventSettings define the valid range and period boundaries.

### 3.3 UI Display

Users see resolved dates/times in the event timezone (e.g., "Tuesday July 7, 10:00–18:00"). The offset is internal. Leads creating shifts pick a date from a calendar (system converts to offset) and set start time + duration.

---

## 4. Signup System

### 4.1 State Machine

```
Public policy:    volunteer signs up → Confirmed (instant)
RequireApproval:  volunteer signs up → Pending → Confirmed (approved) | Refused
Voluntell:        lead/NoInfoAdmin assigns → Confirmed (Enrolled=true)
Any Pending:      volunteer withdraws → Bailed (self-retraction)
Any Confirmed:    volunteer or lead bails → Bailed
Any Confirmed:    lead marks after shift → NoShow
Deactivated duty: GC job after 7 days → Cancelled
```

### 4.2 Invariants (app-side, not transactional)

**On signup creation:**
1. **Overlap check** — block if the volunteer has a Confirmed signup with overlapping absolute times. Show the conflicting shift's title, team, date/time.
2. **Capacity warning** — if `Confirmed` count >= `MaxVolunteers`, show warning. Not a hard block.
3. **EE cap warning** — if the shift is build-period and adding this volunteer would exceed the daily EE capacity for that day offset, show warning.
4. **AdminOnly protection** — only Dept Coordinators, NoInfoAdmin, Admin can sign up for AdminOnly shifts.
5. **System open check** — regular volunteers can only sign up when `IsShiftBrowsingOpen = true`. Dept Coordinators/NoInfoAdmin/Admin bypass this for their own scope (Dept Coordinators: own department only).

**On approval (Pending → Confirmed):**
7. **Revalidation** — all confirmation-time invariants (overlap, capacity, EE cap, EE freeze) are rechecked when a lead approves a Pending signup. If the volunteer now has a conflicting Confirmed shift or the capacity situation has changed, the approver sees a warning.

**After EarlyEntryClose:**
6. **EE freeze** — after `EarlyEntryClose`, only Dept Coordinators, NoInfoAdmin, Admin can bail OR create new build-period (`DayOffset < 0`) signups. Regular volunteers are blocked from both. This protects the gate list from changes after the cutoff.

### 4.3 Who Can Do What

| Action | Volunteer | Dept Coordinator | NoInfoAdmin | Admin |
|--------|-----------|-----------------|-------------|-------|
| Sign up (Public) | When system open | Always | Always | Always |
| Sign up (RequireApproval) | Creates Pending | Creates Confirmed (own dept) | Creates Confirmed | Creates Confirmed |
| Approve/Refuse signups | — | Own dept | Any | Any |
| Voluntell | — | Own dept | Any | Any |
| Bail own signup | Yes (EE freeze applies) | Yes | Yes | Yes |
| Bail others' signups | — | Own dept | Any | Any |
| Mark no-show | — | Own dept | Any | Any |
| Sign up for AdminOnly | — | Own dept | Any | Any |

### 4.4 Soft-Delete Rules

- **Deactivate Rota/Shift** (`IsActive = false`) — hides from browsing, existing signups remain valid
- **Hard-delete Shift** — blocked if any `Confirmed` signups exist. When deletion is allowed (no Confirmed signups), any remaining `Pending` signups are set to `Cancelled`.
- **Hard-delete Rota** — blocked if any child shift has `Confirmed` signups. When deletion is allowed, cascades to delete child shifts (which cancel their Pending signups).

### 4.5 Signup Garbage Collection

Hangfire job runs daily. Sets status to `Cancelled` on signups referencing deactivated shifts (`IsActive = false`) inactive for 7+ days. Audit log captures the cleanup.

### 4.6 Audit Logging

All DutySignup state transitions integrate with the existing `AuditLogEntry` system. New audit actions:
- `DutySignupConfirmed` — volunteer signed up or approved
- `DutySignupRefused` — lead refused a pending signup
- `DutySignupVoluntold` — lead/NoInfoAdmin assigned a volunteer (records who assigned whom)
- `DutySignupBailed` — volunteer or lead bailed a signup
- `DutySignupNoShow` — lead marked a no-show after the shift
- `DutySignupCancelled` — system cancelled (GC or cascade)

Each entry records `ActorUserId`, the signup's `UserId`, `ShiftId`, and the status transition.

### 4.7 No-Show Tracking

After a shift's end time has passed, Dept Coordinators/NoInfoAdmin/Admin can mark individual signups as `NoShow`. No-show history is visible on a volunteer's profile (to all Dept Coordinators, NoInfoAdmin, Admin — cross-department visibility, since no-show patterns are operational data relevant to any coordinator evaluating a signup).

---

## 5. Early Entry

### 5.1 Computed EE List

A volunteer gets Early Entry if they have ≥1 `Confirmed` signup where `DayOffset < 0` (build period).

**EE arrival date:** `GateOpeningDate.PlusDays(MIN(confirmed build DayOffset) - 1)` — one day before their earliest build shift.

**Worked example:** Volunteer's earliest confirmed build shift has `DayOffset = -5`. EE date = `GateOpeningDate.PlusDays(-5 - 1)` = `GateOpeningDate.PlusDays(-6)`. If gate opens July 7, their EE arrival date is July 1 (6 days before gate, 1 day before their first shift on July 2).

### 5.2 Per-Day Capacity (Step Function)

`EventSettings.EarlyEntryCapacity` is a `Dictionary<int, int>` mapping day offset → max people on site.

Example: `{-30: 30, -15: 100, -5: 300}` means:
- Days -30 to -16: max 30 people on site
- Days -15 to -6: max 100 people on site
- Days -5 to -1: max 300 people on site

The set level holds until the next defined level (step function, no interpolation). Days before the first defined key have capacity 0 (no shifts can grant EE for those days).

### 5.3 Shared with Barrios

Both shift-based EE and barrios (camp) EE count toward the same daily site limit. For 2026 MVP, `EventSettings.BarriosEarlyEntryAllocation` (admin-entered per-day barrio headcount) is subtracted: available shift-EE slots = `EarlyEntryCapacity[day] - BarriosEarlyEntryAllocation[day]`. Automated integration with the camps system is deferred.

### 5.4 EE Freeze

After `EventSettings.EarlyEntryClose`, regular volunteers cannot bail OR create new build-period (`DayOffset < 0`) signups. Dept Coordinators, NoInfoAdmin, Admin still can do both. Protects the gate list from changes after the cutoff.

### 5.5 EE Gaming Detection (Placeholder)

Future requirement: detect volunteers with suspiciously light build schedules who may be gaming for an EE pass (e.g., one shift 10 days before gate opening, then nothing). Two potential approaches:
- **Busyness metric:** shift hours per day between first build shift and gate opening — flag low-density schedules
- **MaxEarlyArrivalBuffer:** add a configurable cap (e.g., 2–3 days) to EventSettings so the EE arrival date is `MAX(MIN(shift) - 1, MIN(shift) - buffer)`, preventing someone from getting 14 free days from a single early shift

Implementation deferred — spec this as a report/dashboard feature when needed.

### 5.6 EE Credentials (Future)

Eventually, EE passes become a concrete credential (code/ticket add-on) for gate entry. Flexible for 2026 — the system generates the EE list, but gate integration is not in scope.

---

## 6. Routes & Views

### 6.1 Route Map

| Route | Access | Purpose |
|-------|--------|---------|
| **Volunteer-facing** | | |
| `/` (homepage) | All | Existing dashboard + "My Shifts" card + "Shifts Need Help" card |
| `/Shifts` | Volunteers (when open) | Browse all open shifts, filter by date/department. Sign up. |
| `/Shifts/Mine` | Volunteers | All personal signups (confirmed, pending, bailed, past). iCal feed URL + copy button. |
| `/ICal/{token}.ics` | Anyone with token | Personal iCal feed (anonymous, token-authenticated) |
| **Department Coordinator** | | |
| `/Teams/{slug}` | Team members | Existing page + "Shifts" summary card (fill rates, pending count) |
| `/Teams/{slug}/Shifts` | Dept Coordinators | Create/edit rotas & shifts, approve/refuse signups, fill rates. Only for parent teams (departments). |
| **Cross-team / operational** | | |
| `/Shifts/Dashboard` | NoInfoAdmin, Admin | Urgency-ranked unfilled shifts, date/department filters, voluntell, build/strike staffing visualization |
| `/Shifts/Stats` | NoInfoAdmin, Admin | Aggregate statistics with drill-down links |
| `/Shifts/Exports` | Dept Coordinators (own dept), NoInfoAdmin, Admin | CSV exports: dept/all rota, EE list, cantina setup |
| **Event admin** | | |
| `/Shifts/Settings` | Admin | EventSettings management (dates, caps, toggle system open) |

### 6.2 Homepage Cards (when shift system open)

**"My Shifts" card:**
- Upcoming confirmed shifts (title, department, date/time)
- Count of pending signups
- Link to `/Shifts/Mine`

**"Shifts Need Help" card:**
- Top 3–5 highest-priority unfilled shifts
- Link to `/Shifts` for full browsing

**Existing cards** (profile complete, consents, etc.) — hide once completed/not relevant.

### 6.3 Navigation

New nav item **"Shifts"** (or final name TBD) in the top navbar:
- Visible to all volunteers when `IsShiftBrowsingOpen = true` OR the user has any Confirmed/Pending signups (so they can always reach `/Shifts/Mine` and their existing shifts)
- Always visible to Dept Coordinators, NoInfoAdmin, Admin

### 6.4 NoInfo Dashboard (`/Shifts/Dashboard`)

Single view showing all unfilled shifts (`Confirmed < MaxVolunteers`) sorted by urgency score:

```
remainingSlots = MaxVolunteers - ConfirmedCount
understaffedMultiplier = (ConfirmedCount < MinVolunteers) ? 2 : 1
score = remainingSlots × priorityWeight × durationHours × understaffedMultiplier
```

Priority weights: Normal=1, Important=3, Essential=6. `durationHours` = shift Duration in hours. Shifts below `MinVolunteers` get a 2× urgency boost since they're more critical than shifts that are just not full.

**Filters:** Date picker, department dropdown.

**Each row:** Shift title, department, date/time, slots remaining, priority badge, **Voluntell** button.

**Voluntell flow:** Button opens volunteer search modal → search by name → see skills, current bookings, availability → click to assign → creates Confirmed signup with `Enrolled=true`.

**Build/Strike Staffing Visualization:** Embedded component showing per-day confirmed volunteers vs total slots needed across the build/strike period. Color-coded fill indicators (green/yellow/red).

### 6.5 Department Coordinator View (`/Teams/{slug}/Shifts`)

Only available for parent teams (departments). Shows:

**Sidebar/header:** Department name, fill rate, total volunteer count, pending approval count.

**Actions:** Add rota, export department rota CSV.

**Main content:**
- **Pending approval panel** — `Pending` signups with user info, approve/refuse buttons
- **Rotas** — each rota as a section, showing its shifts in date order. Per-shift: title, date/time, fill status, edit/deactivate controls.
- **No-show marking** — for past shifts, mark individual signups as no-show.

---

## 7. CSV Exports

All exports available at `/Shifts/Exports`. Authorization varies by scope.

### 7.1 Rota CSV (3 scopes)

**Department Rota** (Dept Coordinators for own dept, NoInfoAdmin, Admin):

| Column | Source |
|--------|--------|
| Department | Team.Name (parent team) |
| Shift Title | Shift.Title |
| Date | Resolved date in event timezone |
| Start | Resolved start time |
| End | Resolved end time |
| Duration | Shift.Duration |
| Volunteer Name | User.DisplayName |
| Email | User effective email |
| Ticket ID | TicketAttendee.VendorTicketId where `MatchedUserId = User.Id` (first match if multiple; blank if none) |
| Legal Name | Profile.FullName |

**All Rotas** (NoInfoAdmin, Admin): Same columns across all departments.

### 7.2 Early Entry CSV

| Column | Computation |
|--------|-------------|
| Name | User.DisplayName |
| Email | User effective email |
| Ticket ID | TicketAttendee.VendorTicketId where `MatchedUserId = User.Id` |
| EE Date | `GateOpeningDate.PlusDays(MIN(DayOffset) - 1)` |
| First Shift | Earliest build shift start (event timezone) |
| Last Shift End | Latest build shift end (event timezone) |
| Department Progression | Ordered list of "DeptName (ShiftTitle)" for all build signups |

Available to: Dept Coordinators (own dept), NoInfoAdmin, Admin (all).

### 7.3 Cantina Setup CSV (Admin only)

Admin-only because this is operational planning data (meal purchasing), not coordination data. NoInfoAdmin can view individual dietary info via volunteer event profiles but doesn't need the aggregate export.

Per-day dietary headcounts for each day across build/event/strike:

| Column | |
|--------|--|
| Date | Each day in the period |
| Total Volunteers | Unique confirmed volunteers with shifts overlapping that day |
| Omnivore / Vegetarian / Vegan / Pescatarian | Counts from volunteer event profile |
| Per-allergy counts | Celiac, Shellfish, Nuts, Tree Nuts, Soy, Egg |
| Per-intolerance counts | Gluten, Peppers, Shellfish, Nuts, Egg, Lactose, Other |

---

## 8. iCal Personal Feed

### 8.1 URL & Authentication

`/ICal/{token}.ics` — anonymous endpoint. Token is the user's `ICalToken` Guid (see §2.8). The Guid IS the authentication — no login required.

Token management: user can regenerate from `/Shifts/Mine` (generates new Guid, invalidates old subscriptions). "Copy Feed URL" button on that page.

### 8.2 Content

Standard iCal (RFC 5545):
- `VTIMEZONE` from `EventSettings.TimeZoneId`
- `REFRESH-INTERVAL`: 1 hour during event period, 4 hours otherwise (tunable based on load)

**One `VEVENT` per signup**, with status-dependent rendering. The feed contains only shift scheduling data — no volunteer event profile data (dietary, skills, etc.) is included.

| Signup Status | Event Title | iCal Status | Description |
|---------------|-------------|-------------|-------------|
| Confirmed | "{Title} — {Department}" | CONFIRMED | Shift details, team info |
| Pending | "Pending: {Title} — {Department}" | TENTATIVE | Awaiting approval |
| Bailed | "Bailed: {Title} — {Department}" | CANCELLED | Self-bail: "You bailed on {date}." Lead-bail: "Bailed by {who} on {date}." + reason if any |
| Cancelled | "Cancelled: {Title} — {Department}" | CANCELLED | "Cancelled on {date} (shift deactivated/removed)." (System-only status — always has null actor) |
| NoShow | "No-Show: {Title} — {Department}" | CANCELLED | "Marked as no-show by {who} on {date}" |
| Refused | Not included | — | — |

---

## 9. Email Notifications

### 9.1 Infrastructure

Reuses existing `EmailOutboxMessage` + `ProcessEmailOutboxJob`. New `DutySignupId Guid?` FK on EmailOutboxMessage for deduplication (see §2.6).

### 9.2 Triggers

| # | Trigger | Recipient | When | Opt-out? |
|---|---------|-----------|------|----------|
| 1 | Signup Confirmed | Volunteer | Status → Confirmed (auto or after approval) | No |
| 2 | Signup Refused | Volunteer | Status → Refused | No |
| 3 | Voluntell Assignment | Volunteer | Enrolled signup created | No |
| 4 | Bail/Cancel to Lead | All active Dept Coordinators of the shift's department | Status → Bailed or Cancelled | No |
| 5 | Schedule Change | Volunteer | Lead modifies/deactivates a shift volunteer is signed up for | Yes |
| 6 | Shift Reminder | Volunteer | `ReminderLeadTimeHours` before shift start | No |
| 7 | Mass Reminder | All volunteers with signups | Admin-triggered from `/Shifts/Dashboard` | No |
| 8 | Pending Signup Awaiting Approval | All active Dept Coordinators of the shift's department | New Pending signup created on a RequireApproval rota | No |

### 9.3 Email Context

Each notification includes:
- Personalized footer with the volunteer's next 3–5 confirmed shifts
- iCal feed URL
- Link to `/Shifts/Mine`

### 9.4 Notification Preferences

Lightweight preference on the volunteer event profile:
- `SuppressScheduleChangeEmails` (bool) — opt out of trigger #5

Designed to be extensible for the broader unsubscribe functionality being developed.

### 9.5 Shift Reminder Scheduling

Hangfire job runs periodically. Finds shifts where `AbsoluteStart - now <= ReminderLeadTimeHours` and no reminder email exists for that signup (checked via `(DutySignupId, TemplateName)` on EmailOutboxMessage). Enqueues reminder emails.

---

## 10. Statistics & Monitoring

### 10.1 Stats Page (`/Shifts/Stats`)

Available to NoInfoAdmin, Admin. All stats link to drill-down views where possible.

**Engagement funnel** (using ticket holders as the realistic baseline):
- Ticket holders (matched TicketAttendees)
- With completed profile
- With volunteer event profile
- With ≥1 confirmed signup
- With ≥3 event-time shifts ("super volunteers")
- Department Coordinators count

**Shift coverage:**
- Total shifts / total slots (MaxVolunteers summed)
- Confirmed signups / total slots (fill rate)
- Shifts below MinVolunteers (understaffed) — links to filtered dashboard
- Pending approvals — links to approval queue

**Early Entry:**
- Current EE volunteer count vs daily capacity curve
- Per-day breakdown for build period

**By department:**
- Fill rate per department — links to department shift page
- Pending approvals per department

**Global volunteer cap:**
- Unique volunteers with any confirmed signup vs `GlobalVolunteerCap`
- Warning indicator when approaching

### 10.2 Grafana Monitoring Metrics

Expose operational counters for the external Grafana instance:
- Shifts: total, filled, understaffed
- Signups: by status (confirmed, pending, bailed, noshow)
- iCal feed refresh count
- EE count vs daily capacity
- Pending approvals count
- Email notification queue depth

Exact mechanism (metrics endpoint, Hangfire push job, or log-based) deferred to implementation.

---

## 11. Authorization Matrix

| Capability | Volunteer | Dept Coordinator | NoInfoAdmin | Admin |
|------------|-----------|-----------------|-------------|-------|
| Browse open shifts | When system open | Always | Always | Always |
| Sign up for shifts | When system open | Always | Always | Always |
| Bail own signup | Yes (EE freeze applies) | Yes | Yes | Yes |
| Sign up for AdminOnly | — | Own dept | Any | Any |
| Create/edit rotas & shifts | — | Own dept | — | Any |
| Approve/refuse signups | — | Own dept | Any | Any |
| Voluntell | — | Own dept | Any | Any |
| Mark no-show | — | Own dept | Any | Any |
| Bail others' signups | — | Own dept | Any | Any |
| View NoInfo dashboard | — | — | Yes | Yes |
| View stats | — | — | Yes | Yes |
| Export CSV (dept scope) | — | Own dept | Yes | Yes |
| Export CSV (all) | — | — | Yes | Yes |
| Cantina CSV | — | — | — | Yes |
| Event settings | — | — | — | Yes |
| Toggle system open | — | — | — | Yes |
| Mass reminder email | — | — | — | Yes |
| View volunteer event profiles | — | Own dept signups | Any | Any |
| View iCal feed | Own (via token) | Own | Own | Own |
| Manage notification prefs | Own | Own | Own | Own |

**"Dept Coordinator"** = user with a management role assignment (`TeamRoleDefinition.IsManagement`) on a parent team (department). Management roles on sub-teams do NOT grant Dept Coordinator access to the shift system — only management roles on parent teams (`ParentTeamId IS NULL`) count.

**NoInfoAdmin cannot create/edit rotas or shifts** — this is intentional. NoInfoAdmin is an operational coordination role (fill gaps, voluntell), not a structural role. Only Dept Coordinators and Admin can define the shift schedule. Known limitation: on-site at 2am, if no Dept Coordinator or Admin is reachable, NoInfoAdmin cannot create emergency shifts. Workaround: at least one Admin should be on-site during the event.

**Authorization implementation:** The "Dept Coordinator" check (user has `IsManagement` role on a parent team) is a multi-join query unlike the simple claim-based `[Authorize(Roles = "...")]` used elsewhere. Implement a `ShiftAuthorizationService` with methods like `IsDeptCoordinator(userId, teamId)` and cache the coordinator→department mapping via `IMemoryCache` (at ~500 users, the full mapping fits in RAM). Invalidate on `TeamRoleAssignment` changes.

---

## 12. Build/Strike Staffing Visualization

Dashboard component on `/Shifts/Dashboard` (global) and `/Teams/{slug}/Shifts` (department-scoped).

For each day in the build/strike range:
- Date
- Confirmed volunteers with shifts overlapping that day
- Total slots needed (sum of MaxVolunteers for shifts on that day)
- Visual fill indicator (green = covered, yellow = close, red = understaffed)

---

## 13. Implementation Slices

Built as vertical slices, each independently deployable and useful.

### Slice 1 — Core + Lead Management
- EventSettings entity (including EarlyEntryCapacity, BarriosEarlyEntryAllocation, EarlyEntryClose) and `/Shifts/Settings` admin page
- Rota and Shift entities (with relative offset date system, EventSettingsId FK)
- DutySignup entity with state machine
- `NoInfoAdmin` governance role in RoleNames
- Department Coordinator shift management at `/Teams/{slug}/Shifts`
- Create/edit rotas and shifts, approve/refuse signups, fill rate display
- "Shifts" summary card on `/Teams/{slug}` for departments
- Audit logging for all signup state transitions
- Extend `ProcessAccountDeletionsJob` to handle DutySignup cleanup, ICalToken deletion, and volunteer event profile data deletion

### Slice 2 — Volunteer Experience
- Homepage "My Shifts" and "Shifts Need Help" cards
- `/Shifts` browsing page with date/department filters
- Signup flow (overlap blocking, capacity warning, EE cap warning)
- Bail flow with EE freeze enforcement (EE data from EventSettings created in Slice 1)
- `/Shifts/Mine` personal signup view with iCal feed URL display
- ICalToken Guid? field on User entity (needed for iCal URL generation)
- Volunteer event profile card (skills, quirks, dietary, medical, etc.)
- Notification preferences (schedule change opt-out)

### Slice 3 — NoInfo & Coordination
- `/Shifts/Dashboard` with urgency-ranked unfilled shifts
- Voluntell capability (search modal, assign)
- No-show marking (post-shift)
- Build/strike staffing visualization (on `/Shifts/Dashboard` and as enhancement to `/Teams/{slug}/Shifts`)

### Slice 4 — Exports & Stats
- CSV exports: department rota, all rotas, EE list, cantina setup
- iCal personal feed (`/ICal/{token}.ics`) with cancelled/bailed/noshow history
- `/Shifts/Stats` with drill-down links
- Grafana monitoring metrics

### Slice 5 — Notifications
- 8 email notification triggers via EmailOutboxMessage (including new "Pending Signup Awaiting Approval" trigger)
- DutySignupId FK on EmailOutboxMessage with (DutySignupId, TemplateName) dedup
- Schedule change opt-out enforcement
- Shift reminder scheduling (Hangfire job)
- Signup GC job (Cancelled status for deactivated shifts)

### Slice Dependencies

```
Slice 1 ← Slice 2 (needs entities + EventSettings)
Slice 1 ← Slice 3 (needs entities + signup state machine)
Slice 1 ← Slice 4 (needs entities for export queries)
Slice 1 ← Slice 5 (needs entities for notification triggers)
Slice 2 ← Slice 3 (voluntell uses signup flow)
```

Slices 3, 4, 5 are independent of each other and can be built in parallel after Slice 2.

**Implementation note:** The urgency scoring query used by Slice 2's "Shifts Need Help" homepage card should be implemented as a reusable `ShiftUrgencyService` that Slice 3's NoInfo dashboard also consumes.

---

## 14. Open Items (Deferred)

| Item | Status |
|------|--------|
| Project duty type (multi-day with per-day staffing) | Deferred — use shifts in rotas for 2026 |
| Skills/quirks urgency matching on NoInfo dashboard | Deferred — urgency scoring works on priority + capacity for now |
| Arrival tracking / check-in | Future |
| Year-over-year migration tooling | Not needed — relative offsets enable copy |
| EE gate credential integration | Future — 2026 uses computed list only |
| EE gaming detection report | Placeholder — busyness metric or MaxEarlyArrivalBuffer TBD |
| Automated barrios EE integration | Manual allocation for 2026; automate with camps system later |
| Volunteer event profile storage mechanism | Deferred to implementation |
| Top-level section URL name | TBD — using `/Shifts` as placeholder |
| NoInfoAdmin emergency shift creation | Known limitation — workaround: Admin on-site during event |

---

## Glossary

| Term | Definition |
|------|-----------|
| **Bail** | Volunteer cancels their confirmed signup before the shift. Status → `Bailed`. Different from no-show (post-shift). |
| **Build period** | Days before the event when infrastructure is constructed. `DayOffset < 0`. |
| **Cantina** | Event kitchen. The cantina CSV provides daily dietary headcounts for meal planning. |
| **Day Offset** | Integer representing days relative to gate opening. `-30` = 30 days before, `+2` = day 2 of event. |
| **Department** | A parent team in Humans' team hierarchy. Rotas/shifts belong to departments. |
| **Department Coordinator** | A user with a management role assignment on a parent team. Manages shifts for that department. |
| **Duty** | Generic term for work a volunteer signs up for. Currently only Shifts; Projects may be added later. |
| **Early Entry (EE)** | Permission to arrive on site before gate opening. Auto-computed from confirmed build-period signups. |
| **EE Freeze** | After `EarlyEntryClose`, regular volunteers cannot bail build-period signups. |
| **Enrolled** | Flag on a signup indicating the volunteer was assigned by a lead (voluntold). They can still bail. |
| **Event period** | Main event dates when the public is present. `0 <= DayOffset <= EventEndOffset`. |
| **Gate Opening** | The reference date (day 0) for the entire shift schedule. `EventSettings.GateOpeningDate`. |
| **No-Show** | Lead/admin marks after a shift that the volunteer didn't show up. Status → `NoShow`. |
| **NoInfoAdmin** | Governance role for cross-team operational coordination. Can see all shifts, voluntell anyone, manage any signup. |
| **Priority** | Rota importance level (Normal/Important/Essential). Affects urgency scoring weights: 1/3/6. |
| **Rota** | A container grouping related shifts. Carries the approval policy and priority that child shifts inherit. |
| **Shift** | A single work slot with DayOffset + StartTime + Duration. Belongs to a Rota. |
| **Strike period** | Days after the event for teardown. `DayOffset > EventEndOffset`. |
| **Urgency score** | `(MaxVolunteers - ConfirmedCount) × priorityWeight × durationHours × understaffedMultiplier`. Shifts below MinVolunteers get 2× urgency boost. Ranks unfilled shifts on the NoInfo dashboard. |
| **Voluntell** | Lead directly assigns a volunteer to a shift. Creates Confirmed signup with `Enrolled=true`. Portmanteau of "volunteer" + "tell". |
