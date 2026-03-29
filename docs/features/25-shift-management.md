# Shift Management

## Business Context

Nobodies Collective runs multi-day events (e.g., Nowhere) where volunteers are needed for shifts across departments (Gate, Bar, DPW, etc.). The shift management system lets admins configure event schedules, department coordinators create and manage shift rotas, and volunteers browse and sign up for shifts. Urgency scoring surfaces understaffed shifts to drive volunteer action.

See `docs/specs/shift-management-spec.md` for the full design specification.

## User Stories

### US-25.1: Admin Configures Event
**As an** Admin
**I want to** create and manage EventSettings (dates, timezone, EE capacity, browsing toggle)
**So that** the shift system is configured for the current event cycle

**Acceptance Criteria:**
- Only one active EventSettings at a time
- Configure gate opening date, build/event/strike offsets, timezone
- Set early entry capacity step function and barrios allocation
- Toggle shift browsing open/closed
- Set early entry close instant

### US-25.2: Coordinator Manages Rotas and Shifts
**As a** department coordinator
**I want to** create rotas with shifts for my department
**So that** volunteers can sign up for work slots

**Acceptance Criteria:**
- Create rota with name, period (Build/Event/Strike), priority (Normal/Important/Essential), signup policy (Public/RequireApproval), and optional practical info
- Toggle rota visibility (`IsVisibleToVolunteers`) — hidden rotas are excluded from volunteer browse but remain visible to coordinators/admins, enabling staged rollout of signup
- Build/strike rotas use all-day shifts with date-range signup; event rotas use time-slotted shifts
- Bulk shift creation: `CreateBuildStrikeShiftsAsync` for build/strike rotas (one all-day shift per day offset with per-day staffing), `GenerateEventShiftsAsync` for event rotas (Cartesian product of day offsets x time slots)
- Create individual shifts with day offset, start time, duration, min/max volunteers
- Mark shifts as AdminOnly to restrict to coordinators/admins
- Delete rotas/shifts (delete blocked if confirmed signups exist; no deactivate — delete is the only removal path)

### US-25.3: Volunteer Browses and Signs Up
**As a** volunteer
**I want to** browse available shifts and sign up
**So that** I can contribute to the event

**Acceptance Criteria:**
- Browse shifts filtered by department and date range (From/To date pickers; either may be omitted for open-ended range)
- Filter by period (Set-up / Event / Strike toggle buttons)
- Date picker and period tabs interact cleanly: selecting a date clears the active period tab (dates take precedence); selecting a period tab clears manual dates; date picker min/max constrains to the active period's range when a period is selected
- Consecutive all-day shifts within the same rota are compressed into date ranges (e.g., "Jun 16–21, 6 days") with aggregated fill status; click to expand individual days
- Only rotas with `IsVisibleToVolunteers = true` appear (privileged users see all)
- See fill status (confirmed count vs max)
- Sign up for a shift (auto-confirmed for Public policy, pending for RequireApproval)
- Date-range signup for build/strike rotas via `SignUpRangeAsync` — creates signups for all all-day shifts in the range, linked by a shared `SignupBlockId`
- Overlap detection prevents signing up for conflicting time slots (event shifts)
- AdminOnly shifts hidden from non-privileged users
- EE freeze blocks non-privileged build shift signups after early entry close

### US-25.4: Coordinator Approves/Refuses Signups
**As a** department coordinator
**I want to** approve or refuse pending signups
**So that** I can manage who works my department's shifts

**Acceptance Criteria:**
- Approve re-validates overlap (returns warning, not blocker)
- Refuse with optional reason
- Batch approve/refuse: `ApproveRangeAsync`/`RefuseRangeAsync` — approves or refuses all pending signups sharing a `SignupBlockId` in one action
- Pending approvals table shows signup date and groups range signups with date range display
- Voluntell: enroll a volunteer directly (auto-confirmed, sets Enrolled flag)
- Remove: unassign a confirmed signup via `RemoveSignupAsync` — transitions to Cancelled with reviewer tracking

### US-25.5: Volunteer Manages Their Shifts
**As a** volunteer
**I want to** view my shifts and bail if needed
**So that** I can manage my schedule

**Acceptance Criteria:**
- View upcoming, pending, and past shifts on /Shifts/Mine
- Bail from confirmed or pending signups
- Range bail via `BailRangeAsync` — bails all signups sharing a `SignupBlockId` (build/strike date-range signups)
- Build shift bail blocked after EE close for non-privileged users
- Reusable `ShiftSignupsViewComponent` shows categorized signups (upcoming/pending/past) on Dashboard and HumanDetail pages

### US-25.7: Guided Shift Discovery
**As a** volunteer with no upcoming shifts
**I want to** see a guided introduction to shifts on my dashboard
**So that** I understand how to get involved

**Acceptance Criteria:**
- When shift browsing is open and user has no upcoming or pending signups, Dashboard shows a discovery card
- Discovery card explains the three shift phases (Set-up, Event, Strike) with brief descriptions
- Urgent understaffed shifts are highlighted within the discovery card
- Clear CTAs to browse all shifts and view own shift schedule
- When user has existing signups, the standard shift signups component and urgent shifts list are shown instead

### US-25.6: Post-Event No-Show Tracking
**As a** coordinator
**I want to** mark no-shows after shifts end
**So that** reliability data is captured

**Acceptance Criteria:**
- MarkNoShow blocked before shift end time
- Sets status to NoShow with reviewer recorded

## Data Model

| Entity | Purpose |
|--------|---------|
| `EventSettings` | Singleton event config: dates, timezone, EE capacity, browsing toggle |
| `Rota` | Shift container per department+event, with period (Build/Event/Strike), priority, signup policy, practical info, and visibility toggle (`IsVisibleToVolunteers`, default true) |
| `Shift` | Single work slot: day offset, time, duration, volunteer min/max; IsAllDay flag for build/strike shifts |
| `ShiftSignup` | User-to-shift link with state machine; SignupBlockId groups range signups |
| `GeneralAvailability` | Per-user per-event day availability (general volunteer pool) |
| `VolunteerEventProfile` | Per-event skills, dietary, medical info, email preferences |

## State Machine (ShiftSignup)

```
Pending --> Confirmed   (Approve / auto-confirm)
Pending --> Refused     (Refuse)
Pending --> Bailed      (Bail)
Confirmed --> Bailed    (Bail)
Confirmed --> NoShow    (MarkNoShow, post-shift only)
Confirmed --> Cancelled (system: shift deleted, account deletion, or coordinator removal)
Pending --> Cancelled   (system: shift deleted, account deletion)
```

## Authorization Model

| Role | Permissions |
|------|------------|
| Admin | Full access: manage shifts, approve signups, bypass all restrictions |
| NoInfoAdmin | Approve/refuse signups, voluntell; cannot create/edit shifts or rotas |
| Dept Coordinator | Manage rotas/shifts for own department, approve/refuse signups |
| Volunteer | Browse shifts, sign up, bail, view own schedule |

## Urgency Scoring

`score = remainingSlots * priorityWeight * durationHours * understaffedMultiplier * proximityBoost`

- Priority weights: Normal=1, Important=3, Essential=6
- Understaffed multiplier: 2x when confirmed < minVolunteers, else 1x
- Proximity boost: `1 + 10 / (1 + daysUntilStart)` — today ~11x, tomorrow ~6x, 7 days ~2.25x, 30 days ~1.3x
- Score=0 when fully staffed (remaining=0)

## Routes

| Route | Purpose |
|-------|---------|
| `/Shifts` | Browse all shifts (filtered by department, date range, period) |
| `/Shifts/Mine` | View own signups (upcoming, pending, past) |
| `/Shifts/Settings` | Admin: manage EventSettings |
| `/Teams/{slug}/Shifts` | Coordinator: manage rotas/shifts for a department |
| `/` (Dashboard) | Shift signups ViewComponent + guided discovery when no signups |
| `/Human/{id}/Admin` | Shift signups ViewComponent (admin view of user's shifts) |

## Related Features

- **Teams** (06): Departments are parent teams; coordinator roles grant shift management access
- **Profiles** (02): VolunteerEventProfile extends the user profile with event-specific data
- **Audit Log** (12): All signup state transitions are audit-logged
