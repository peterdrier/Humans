# VIM Shift Management → Humans: Design Specification

## Context

VIM (Volunteer Information Manager) is a Meteor-based volunteer management system for the Elsewhere event. This spec covers what must be built in Humans to subsume VIM's shift creation, filling, and management functionality — focusing on features that don't already exist in Humans.

**What Humans already has** that maps to VIM concepts:
- Profiles (1:1 with users, bio, emergency contact, etc.)
- Teams with join/leave/approval workflows
- Team Role Definitions with slot counts and priority levels (including auto-created Lead slot per team)
- Governance roles (Admin, Board, etc.) with temporal validity
- Audit logging, background jobs (Hangfire), Google Workspace sync
- Email outbox (`EmailOutboxMessage` + `ProcessEmailOutboxJob` + `email_outbox_paused` SystemSetting)

---

## 1. Org Hierarchy Extension

### Gap
VIM has a three-tier hierarchy: **Division → Department → Team**. Humans has only Teams (flat). VIM's departments exist primarily to scope lead authority and aggregate reporting — a metalead sees all teams in their department, a manager sees everything.

### Design
Add `Department` as a new entity above `Team`. Each event team belongs to a department. Division is dropped — VIM's own code confirms it's vestigial at Elsewhere's scale (one division, always). If multi-division support is needed later, add an optional `ParentDepartmentId` self-FK.

**New entity:**

```
Department
├── Id (Guid)
├── Name
├── Description
├── IsActive (bool)
├── SortOrder (int)
├── CreatedAt, UpdatedAt
```

**Team changes:**
- `Team` gets a nullable `DepartmentId` FK (nullable so existing system teams — Volunteers, Leads, Board, Asociados, Colaboradors — aren't forced into the hierarchy)
- Department and Team occupy **separate slug spaces** — departments use ID-based URL routing (`/department/{id}`), not slugs. This avoids collision with the existing `Team.Slug` unique index.

**New governance roles:**
- `MetaLead` — scoped to a department. Add to `RoleNames.cs`. Metalead RoleAssignment needs a `ScopedEntityId` (or similar) to indicate which department.
- `NoInfo` — operational coordination role (see §8). Add to `RoleNames.cs` as a governance role (not a system team) since it grants cross-team operational access.

**Impact on existing features:** Team Role slots and team membership are unaffected. The hierarchy is purely for scoping views and aggregating reports.

**Phase:** Department must ship with Phase 1 — lead dashboards, rota exports, and metalead views all require department scoping.

---

## 2. Duty Types (Shifts and Projects)

### Core Concept
Two distinct duty types sharing a common signup system but with different scheduling semantics:

| Type | Time Model | Use Case |
|------|-----------|----------|
| **Shift** | Fixed `start`/`end` datetime (typically 4-8h) | Event-time operational work |
| **Project** | Date range with per-day `min`/`max` staffing array | Build/strike multi-day work |

### Lead Positions — Use Existing TeamRoleDefinition System

VIM has a third "Lead" duty type because it has no separate concept of role slots. **Humans already has `TeamRoleDefinition` + `TeamRoleAssignment`** with an auto-created Lead slot per team, where assignment sets `TeamMember.Role = Lead` (the permission source of truth).

Adding a separate `LeadPosition` entity would create a dual-authority conflict. Instead:
- **Lead applications** use the existing `TeamJoinRequest` system, targeting the Lead role definition slot. A volunteer applies → request sits in `Pending` → metalead approves → `TeamRoleAssignment` is created → `TeamMember.Role = Lead`.
- **"Pending lead count"** on dashboards = count of `TeamJoinRequest` where the target is a Lead role slot and status is `Pending`.
- **Lead fill rate** = count of `TeamRoleAssignment` for Lead slots vs `TeamRoleDefinition.SlotCount`.

This eliminates `LeadPosition` as a duty type entirely and avoids bidirectional sync problems between signup status and role assignment state.

### New Entities

```
Rota (shift group/container)
├── Id (Guid), TeamId (FK)
├── Name, Description
├── StartDate, EndDate (LocalDate — date range the rota covers)
├── Priority: Normal | Important | Essential
├── Policy: Public | RequireApproval
├── Skills (List<string>, stored as jsonb — tags for urgency matching)
├── Quirks (List<string>, stored as jsonb — tags for urgency matching)
├── IsActive (bool — soft-delete, default true)
├── CreatedAt, UpdatedAt
└── Shifts[] (child navigation)

Shift
├── Id (Guid), RotaId (FK)
├── Title, Description
├── StartUtc, EndUtc (Instant — NodaTime)
├── MinVolunteers, MaxVolunteers (int)
├── AdminOnly (bool — hidden from regular volunteers)
├── IsActive (bool — soft-delete, default true)
├── CreatedAt, UpdatedAt
└── Computed: Duration, IsEarlyEntry (start < event open date in event timezone)

Project
├── Id (Guid), TeamId (FK)
├── Title, Description
├── StartDate, EndDate (LocalDate range)
├── Policy: Public | RequireApproval
├── Priority: Normal | Important | Essential
├── Skills (List<string>, stored as jsonb)
├── Quirks (List<string>, stored as jsonb)
├── AdminOnly (bool)
├── IsActive (bool — soft-delete, default true)
├── CreatedAt, UpdatedAt
└── Staffing[] (child collection)

ProjectStaffing (per-day staffing targets)
├── Id (Guid), ProjectId (FK)
├── Date (LocalDate)
├── MinVolunteers, MaxVolunteers (int)
```

### Task Duty Type — Explicit Deferral

VIM has a fourth duty type, **Task** (one-off work items), which is rarely used. It is functionally identical to a single Shift in a Rota. For Humans, model any task-type work as a Shift. If VIM data is migrated, map existing task signups to shift signups. No separate entity needed.

### Skills/Quirks Storage Pattern

Store as `jsonb` arrays using `List<string>` with the same `JsonSerializer` + `ValueComparer` pattern used by `CampSeason.Vibes`. The values must be from a **managed list** (not free-form tags) — the UI presents a fixed multi-select, and values must match between duties and volunteer profiles for urgency scoring to work. The managed list is enforced at the application layer, not the database.

### Duty Deletion Policy

- **Restrict deletion** of any Shift, Project, or Rota that has `Confirmed` signups. Return an error instructing the lead to bail signups first.
- **Cascade deletion** of `Pending` signups when a duty is deleted.
- **Soft-delete** via `IsActive = false` hides a duty from volunteer browsing without losing data. Leads can deactivate duties with confirmed signups (the signups remain valid but the duty no longer appears for new signups).

---

## 3. Signup System

### Lifecycle State Machine

```
                    ┌─── confirmed ───► bailed
apply ──► pending ──┤
                    └─── refused
```

- `Public` policy: skip `pending`, go straight to `confirmed`
- `RequireApproval` policy: sit in `pending` until lead approves/refuses
- `Voluntell` (lead-initiated): direct `confirmed` with `Enrolled=true` flag

### Entity Design

```
DutySignup
├── Id (Guid)
├── UserId (FK)
├── ShiftId (Guid?, FK — nullable)
├── ProjectId (Guid?, FK — nullable)
├── Status: Pending | Confirmed | Refused | Bailed | Cancelled
├── Enrolled (bool — true if lead-assigned via voluntell, not self-signup)
├── Reviewed (bool — lead has reviewed this signup)
├── ProjectStartDate, ProjectEndDate (LocalDate? — for project sub-range signups, only valid when ProjectId is non-null)
├── CreatedAt, UpdatedAt
├── ReviewedByUserId (Guid?), ReviewedAt (Instant?)
└── EnrolledByUserId (Guid? — who voluntold them, only valid when Enrolled=true)
```

**FK design:** Three nullable FKs (`ShiftId`, `ProjectId`) — exactly one must be non-null per row. This replaces the polymorphic `DutyType + DutyId` pattern which doesn't support real FK constraints in EF Core. Add a service-layer validation: exactly one FK is non-null. A computed `DutyType` property can be derived from which FK is populated.

**No `NotificationSent` flag.** Humans already has `EmailOutboxMessage` with deduplication. Instead of a mutable boolean on `DutySignup`, check for existence of a sent/queued `EmailOutboxMessage` linked to the signup. Add `DutySignupId Guid?` FK to `EmailOutboxMessage` for this purpose. This avoids the known VIM weakness of per-signup notification booleans.

### Invariants (enforced server-side)

**On signup creation:**
1. **No double-booking** — no overlapping time ranges across all `Confirmed` shift signups for the same user
2. **Capacity check** — `Confirmed` count < `MaxVolunteers` (or per-day max for projects). **Must be enforced within a transaction** — use `BeginTransaction` + count check + insert + `Commit`. At ~500 users concurrent signup races are unlikely but this is a correctness issue.
3. **Project date constraint** — signup date range must be within project date range
4. **AdminOnly protection** — only leads/managers can sign up for admin-only duties

**On any signup mutation (create, bail, cancel):**
5. **Early entry window** — after `EarlyEntryClose`, only leads/managers can modify early-entry signups. This applies to bails and cancellations, not just creation.

### Bail Flow

The bail operation (volunteer cancelling after confirmation) has specific rules:

- **Who can bail:**
  - The volunteer can bail their own signup (unless blocked by early entry window rule)
  - A lead can bail any signup in their team
  - A NoInfo lead can bail any signup
  - Enrolled (voluntold) users CAN bail themselves — the voluntell is an assignment, not a prison sentence
- **Effects of bail:**
  - Status changes to `Bailed`, freeing the slot (capacity recalculated)
  - The team lead is **notified** that capacity opened (via email outbox)
  - If the duty is now below `MinVolunteers`, it appears with higher urgency on the NoInfo dashboard
- **Early entry bails** are subject to invariant 5 — after `EarlyEntryClose`, only leads can bail early-entry signups

### Signup Garbage Collection

Hangfire job runs periodically to set status to `Cancelled` on signups referencing deactivated duties (`IsActive = false`) that have been inactive for more than 7 days. The audit log captures the original signup data. No separate backup table needed — Humans' audit logging is the record of truth.

---

## 4. Profile Extensions for Volunteer Data

### Decision: Add to Profile Entity

For rapid deployment, add volunteer event fields directly to the existing `Profile` entity. This is simpler (single migration, no joins on profile reads) and consistent with how Profile is already used. If event-specific data isolation is needed later, these fields can be extracted to a `VolunteerEventProfile` entity.

### New Fields on Profile

**For urgency matching / operational use:**
- `Skills` — `List<string>`, stored as `jsonb`. Managed multi-select (e.g., "bartending", "first aid", "driving", "sound")
- `Quirks` — `List<string>`, stored as `jsonb`. Managed multi-select (e.g., "sober shift", "work in shade", "night owl")
- `LanguagesSpoken` — `List<string>`, stored as `jsonb` (English, French, Spanish, German, Italian, other)

**For cantina/catering export:**
- `DietaryPreference` — enum: `Omnivore | Vegetarian | Vegan | Pescatarian`
- `Allergies` — `List<string>`, stored as `jsonb`. Values: Celiac, Shellfish, Nuts, TreeNuts, Soy, Egg
- `Intolerances` — `List<string>`, stored as `jsonb`. Values: Gluten, Peppers, Shellfish, Nuts, Egg, Lactose, Other

**For safety:**
- `MedicalConditions` — free text, **restricted visibility**

**For identity:**
- `TicketId` — string, external ticketing system reference
- `TicketInfo` — string, raw ticket vendor response data (JSON blob)

### Medical Data Access Control

`MedicalConditions` requires explicit visibility rules beyond what the current profile system provides:
- **Visible to:** The profile owner, NoInfo leads (for on-site safety decisions), Managers, Admins
- **NOT visible to:** Other volunteers, team leads (unless they are also NoInfo), board members without operational roles
- This should be enforced at the service layer — `ProfileService` should strip `MedicalConditions` from the response unless the requesting user has one of the allowed roles. The field is stored in the same table but filtered on read.

---

## 5. Event Configuration

### New Entity: `EventSettings`

```
EventSettings
├── Id (Guid)
├── EventName (string, e.g., "elsewhere2026")
├── TimeZoneId (string, IANA timezone, e.g., "Europe/Paris")
├── EventPeriod: { Start, End } (LocalDate — main event dates)
├── BuildPeriod: { Start, End } (LocalDate — pre-event setup)
├── StrikePeriod: { Start, End } (LocalDate — post-event teardown)
├── EarlyEntryMax (int — total early arrival passes available)
├── EarlyEntryClose (Instant — cutoff for early-entry signup changes)
├── EarlyEntryRequirementEnd (Instant — when EE credential checks stop)
├── BarriosArrivalDate (LocalDate — neighborhood/camp arrival start)
├── SystemOpenDate (Instant — when the system opens to regular volunteers, replaces VIM's "FistOpenDate")
├── NotificationSchedule (string — cron expression for batch notification frequency, e.g., "0 */6 * * *")
├── PreviousEventName (string? — for migration source reference)
├── IsActive (bool)
├── CreatedAt, UpdatedAt
```

**`TimeZoneId`** is critical. All date-local computations must use `DateTimeZone.ForId(settings.TimeZoneId)`:
- EE date calculation: `MIN(shift.StartUtc).InZone(tz).Date.Minus(Period.FromDays(1))`
- Cantina daily counts: "volunteers on site today" requires converting shift `Instant` → `LocalDate` in the event timezone
- A shift starting at `2026-07-08T22:00:00Z` is actually on `2026-07-09` in `Europe/Paris`

**`SystemOpenDate`** gates the entire volunteer-facing UI. Before this date, only existing leads and admin-domain users can access shift browsing and signup. After it, all verified users can browse and sign up.

**Email approval** does not need a field here — Humans' existing `email_outbox_paused` SystemSetting serves the same purpose as VIM's `EmailManualCheck`. The only addition needed is a "Regenerate" action on queued emails (re-render template with current data).

---

## 6. Volunteer-Facing Shift Browsing & Signup

### Gap Addressed
This section covers the primary user journey — how regular volunteers find and sign up for shifts. This is the highest-traffic feature.

### Volunteer Dashboard (`/dashboard` additions)

**Booked Table:** Shows all the volunteer's current signups (confirmed + pending), grouped by type:
- Shifts: title, team name, date/time, status badge
- Projects: title, team name, date range, status badge
- Each row has a **Bail** button (for confirmed signups, subject to early entry window rules)

**"Shifts Need Help" Panel:** Filterable list of open duties (where `confirmed < max`), showing:
- Filter tabs: All | Event-time | Build/Strike
- Each duty row: team name, title, date/time, slots remaining (`max - confirmed`), priority badge
- **Sign Up** button per duty — for `Public` policy, immediately confirms. For `RequireApproval`, creates pending signup with feedback message.

### Department/Team Browse Pages

**Department listing** (`/departments`): All active departments with team counts and overall fill rates.

**Team page** (`/teams/{slug}`): Existing team page extended with a **Duties** tab showing:
- Rotas with their shifts (grouped by rota, sorted by date)
- Projects
- Fill status per duty
- Sign Up buttons (same behavior as dashboard)
- Pending status indicator if the volunteer already has a pending signup

### Signup Flow

1. Volunteer clicks **Sign Up** on a duty
2. Service validates all invariants (§3)
3. If `Public` policy → `Confirmed` immediately, success toast
4. If `RequireApproval` → `Pending`, info toast ("Your signup is pending lead approval")
5. If validation fails → error toast with reason (double-booked, full, etc.)
6. Dashboard updates to reflect new signup

---

## 7. Reports & Exports

This is where VIM has the most functionality that's non-obvious from the outside.

### 7a. Rota CSV Exports (3 scope levels)

**Team Rota** (team lead+): All confirmed shift and project signups for a single team.

| Column | Source |
|--------|--------|
| Shift title | Shift/Project.Title |
| Start | Signup start datetime (converted to event timezone) |
| End | Signup end datetime (converted to event timezone) |
| Volunteer name | User display name |
| Email | User email |
| Ticket ID | Profile.TicketId |
| Legal name | User full legal name |

**Department Rota** (metalead+): Same columns plus a `Team` column, covering all teams in the department.

**All Rotas** (manager only): Every team across the entire event. Same columns plus `Team`.

**Purpose:** Primary operational documents. Printed and posted at info desks, given to security for verification, used for insurance/liability records.

### 7b. Early Entry CSV

**Scope:** Lead+ for own team/dept, manager for all.

This is a **computed report**, not a simple data dump. For each user with confirmed build-period signups:

| Column | Computation |
|--------|-------------|
| Name | Display name |
| Email | User email |
| Ticket ID | Profile.TicketId |
| EE Date | `MIN(shift.start) - 1 day` in event timezone — their earliest allowed arrival |
| Overall Start | Earliest signup start (event timezone) |
| Overall End | Latest signup end (event timezone) |
| Team Progression | Ordered list of `"TeamName (DutyTitle)"` for all their build signups |
| Date Ranges | Comma-separated individual date ranges |

**Purpose:** Given to the gate team / early entry coordinator. Source of truth for who is allowed on site before the event opens.

### 7c. Cantina Setup CSV (Manager only)

The most complex export. For **each day** of the build period, counts confirmed volunteers by dietary category:

| Columns per day row |
|---|
| Date |
| Total confirmed volunteers on site that day |
| Omnivore count |
| Vegetarian count |
| Vegan count |
| Pescatarian count |
| Per-allergy counts (celiac, shellfish, nuts, treenuts, soy, egg) |
| Per-intolerance counts (gluten, peppers, shellfish, nuts, egg, lactose, other) |

**How it works:** For each day in the build period, find all shift/project signups where the day falls within the signup's date range (using event timezone for shift→date conversion), join to Profile for dietary data, aggregate. 19 columns total.

**Purpose:** The kitchen/cantina team uses this to plan meals. During build, the number of people on site ramps from ~5 to ~200 over two weeks. Knowing exactly how many vegans are there on Tuesday vs Friday determines purchasing.

### 7d. Build & Strike Staffing Report (Visual, not CSV)

A dashboard component embedded in both department and manager views showing project staffing coverage over time. For each day in the build/strike period, shows volunteers signed up vs needed. Visualizes staffing gaps.

**Purpose:** Lets metaleads see at a glance if Tuesday of build week has 3 people signed up but 15 needed.

### 7e. Org Structure JSON Export/Import (Manager only)

Full serialization of the org tree: departments, teams, rotas, shifts, projects, and confirmed lead assignments. Used for:
- Offline editing (export → edit JSON → reimport)
- Year-to-year migration template
- Backup

---

## 8. Admin/Manager Views

### 8a. Manager Dashboard

**Global statistics panel:**
- Leads: confirmed / needed (across all teams, from TeamRoleDefinition Lead slots)
- Metaleads: confirmed / needed
- Shifts: total booked / total available (all shifts across event)
- Pending approvals count

**Action buttons:**
- Add department (org structure management)
- Export all rotas CSV
- Export early entry CSV
- Export cantina setup CSV
- Send mass reminder emails to all volunteers
- Trigger new-event migration (year rollover)
- Event settings link

**Embedded:** Build & Strike Staffing Report (global scope)

### 8b. Email Queue Management (Manager only)

Humans' existing `EmailOutboxMessage` + `ProcessEmailOutboxJob` infrastructure handles this. The only new capabilities needed:
- **Signup lifecycle triggers** that enqueue emails (voluntell notification, approval/refusal notification, shift reminder, bail notification to lead)
- **Regenerate action** on queued emails — re-render the template with current data (e.g., if a volunteer's duties changed between queueing and review)
- The existing `email_outbox_paused` SystemSetting serves as VIM's `EmailManualCheck` — when paused, managers review queued emails before sending

### 8c. Manager User List

Full paginated user search with:
- Per-user action buttons: send invitation, send review summary, send shift reminder
- Role management: grant/revoke manager, grant/revoke admin
- Search/filter by name, email

### 8d. User Statistics Sidebar

Available on both Manager and NoInfo user lists. Aggregate counts:

| Stat | Description |
|------|-------------|
| Registered | Total user count |
| Profile completed | Users who filled out volunteer form fields |
| With picture | Users who uploaded a profile photo |
| Ticket holders | Users with verified ticket IDs |
| With duties | Users with ≥1 confirmed signup |
| Leads | Users holding lead role assignments |
| With event-time shifts | Users with ≥1 event-period signup |
| With 3+ event-time shifts | "Super volunteers" metric |

Note: VIM's "Online now" metric relied on Meteor's real-time DDP connections. Deferred — would require SignalR for equivalent functionality. Not essential for operations.

---

## 9. NoInfo / Operational Coordination Views

The **NoInfo** role is the most operationally interesting feature. NoInfo leads are on-site coordinators who fill gaps in real-time.

### 9a. NoInfo Dashboard — Urgency-Ranked Open Duties

**Two separate views** accessed via different routes:
- `/noinfo` — event-period unfilled duties
- `/noinfo/strike` — strike-period unfilled duties

Each view shows all unfilled duties (shifts and projects where `confirmed < max`) within the relevant period, sorted by **urgency score**:

```
Priority weights: Normal=1, Important=3, Essential=6

Shift score = maxRemaining × shiftHours × (1 + preferenceScore)
            + minRemaining × priorityWeight × shiftHours × (1 + preferenceScore)

Project score = maxRemaining × (1 + preferenceScore)
              + minRemaining × priorityWeight × (1 + preferenceScore)
```

Where `preferenceScore` = count of matching skills/quirks between the duty's tags and the viewing user's profile tags.

**Key feature: Voluntell button** — each open duty row has a button that opens a volunteer search modal. The NoInfo lead searches for a suitable volunteer (seeing their skills, current bookings, availability), and the system creates a `Confirmed` signup with `Enrolled=true`. The volunteer receives a "you've been assigned" notification via email outbox.

### 9b. NoInfo User List

Same user search as Manager User List, but with a **full profile modal** showing:
- Profile photo, basic info
- Complete volunteer form data (skills, quirks, dietary, languages, medical conditions)
- **Booked Table** — all current shift/project bookings for this user
- Allows the NoInfo lead to assess who's available and what they're already doing before voluntelling them

### 9c. Operational Purpose

The NoInfo system exists because at an event with 200+ volunteers and 100+ shifts:
- People don't show up
- Shifts get added last-minute
- The person running the bar at 2am needs a replacement NOW

The urgency scoring + voluntell capability turns this from "frantically texting people" into "look at ranked list, pick best match, assign with one click."

---

## 10. Lead & MetaLead Views

### 10a. Team Lead Dashboard (`/lead/team/:teamId`)

**Sidebar:**
- Team name and current lead(s) (from TeamRoleAssignment)
- Fill rate: `confirmed / needed` for shifts
- Total volunteer count (unique users with signups)
- Pending approval count

**Actions:**
- Edit team settings
- Add rota (shift group)
- Add project
- Export team rota CSV

**Main content:**
- **Pending signup approval panel** — list of `Pending` signups sortable by created/start/end time. Each row shows user info (via profile modal) with approve/refuse buttons.
- **Tabbed duty summary** — rotas as tabs, each showing a date-strip navigator and shift table. Projects in a separate tab.
- Shift/project editing: leads can edit duty details, adjust min/max, change policy, deactivate duties.

### 10b. Department Dashboard (`/metalead/department/:deptId`)

**Stats:** Team count, metalead fill rate, lead fill rate, shift fill rate, volunteer count, pending count.

**Content:**
- Build & Strike Staffing Report (department scope)
- Team list with per-team pending counts and fill rates
- Pending lead application approvals (from `TeamJoinRequest` targeting Lead role slots in this department's teams)
- Early Entry management panel (see below)
- Export: department rota CSV, early entry CSV

### 10c. Early Entry Management Panel

Displayed on department dashboard (scoped to department) and manager dashboard (global scope). Shows:
- List of users with confirmed build-period signups
- Per-user computed EE date (`MIN(shift.start) - 1 day` in event timezone)
- Total EE count vs `EventSettings.EarlyEntryMax` — visual indicator when approaching/exceeding the cap
- Read-only view (the EE list is computed from signups, not manually managed)

---

## 11. Notification System

### Existing Infrastructure

Humans' `EmailOutboxMessage` + `ProcessEmailOutboxJob` is the target infrastructure. The new work is writing signup lifecycle triggers and templates, not building a new queue.

### New Entity Change

Add `DutySignupId Guid?` FK to `EmailOutboxMessage` — used for deduplication (check if a notification was already queued for a given signup) and for the "Regenerate" action (re-render with current signup context).

### Notification Triggers (new Hangfire jobs)

| Trigger | Template | Recipient | When |
|---------|----------|-----------|------|
| Voluntell | "You've been assigned to [duty]" | The voluntold volunteer | On `Enrolled=true` signup creation |
| Approval | "Your signup for [duty] was approved" | The volunteer | On status change to `Confirmed` from `Pending` |
| Refusal | "Your signup for [duty] was not approved" | The volunteer | On status change to `Refused` |
| Shift reminder | "Your shift [duty] starts in X hours" | The volunteer | Scheduled before shift start |
| Bail notification | "[Volunteer] bailed from [duty] — slot now open" | The team lead(s) | On status change to `Bailed` |
| Mass reminder | Personalized duty summary | All volunteers with signups | Manager-triggered |

**Email context:** Each notification should include a personalized summary of the user's current duties (team names, shift times, project dates).

**Batch processing:** Notification triggers enqueue `EmailOutboxMessage` records. The existing `ProcessEmailOutboxJob` handles sending with retry logic. No inline sending on signup mutations.

---

## 12. Event Migration / Year Rollover

### Purpose
Each year's event needs a fresh set of shifts/rotas/projects but wants to preserve the org structure. VIM handles this with a "new event" migration that:

1. Clones the org structure (department → team → rota → shift/project)
2. Shifts all dates by the delta between old and new event start dates
3. Preserves confirmed lead assignments (so leads carry over)
4. Clears all user ticket IDs (tickets are per-year)
5. Resets signup data (all non-lead signups cleared)

### Design for Humans
Implemented as a Hangfire job triggered from an admin page. The org hierarchy, shift templates, and rota structures are valuable year-over-year and shouldn't require manual recreation.

---

## 13. Feature Priority for Implementation

### Phase 1: Core Scheduling (MVP)
1. Department entity + nullable FK on Team
2. Event Settings (dates, periods, timezone)
3. Rota, Shift, Project entities with soft-delete
4. DutySignup entity with three nullable FKs, invariant enforcement (including transactional capacity check)
5. Volunteer dashboard additions (booked table, open shifts browser, signup flow)
6. Team lead dashboard (create rotas/shifts/projects, approve/refuse signups)
7. Basic fill-rate display on team pages

### Phase 2: Reporting & Exports
8. Rota CSV exports (team, department, all)
9. Early Entry CSV export (with timezone-aware date computation)
10. Build & Strike staffing report (visual)
11. Profile extensions (dietary, skills, quirks, medical with access control)
12. Cantina setup CSV export

### Phase 3: Operational Tools
13. MetaLead governance role + department dashboard
14. NoInfo governance role + urgency-ranked dashboard (event + strike views)
15. Voluntell capability
16. User statistics sidebar
17. Early Entry management panel

### Phase 4: Admin & Lifecycle
18. Signup lifecycle email notifications (voluntell, approval, bail, reminders)
19. Manager dashboard with global stats
20. Event migration / year rollover
21. Manager user list with bulk actions
22. Org structure JSON export/import

---

## 14. Resolved Design Decisions

| Decision | Resolution | Rationale |
|----------|-----------|-----------|
| Division entity | **Dropped** | VIM confirms it's vestigial at Elsewhere's scale. Add `ParentDepartmentId` later if needed. |
| LeadPosition duty type | **Dropped** — use existing TeamRoleDefinition | Avoids dual-authority conflict between signup status and role assignment. Humans already has the lead slot system. |
| DutySignup FK pattern | **Three nullable FKs** (`ShiftId`, `ProjectId`) | Polymorphic `DutyType + DutyId` doesn't support EF Core FK constraints. Nullable FKs match existing Humans patterns. |
| Skills/Quirks storage | **jsonb arrays** via `List<string>` | Matches `CampSeason.Vibes` pattern. Must be managed list for urgency matching. |
| NotificationSent flag | **Dropped** — use `EmailOutboxMessage` dedup | Avoids known VIM weakness. Existing outbox infrastructure handles this better. |
| Profile vs VolunteerForm entity | **Add fields to Profile** | Simpler for rapid deployment. Single migration, no joins. Can extract later. |
| Task duty type | **Model as Shift** | Functionally identical. Map VIM task data to shifts during migration. |
| Email approval queue | **Use existing outbox** | `email_outbox_paused` + `ProcessEmailOutboxJob` already exists. Only need signup triggers and Regenerate action. |
| Department slugs | **No slugs — use ID routing** | Avoids collision with Team's unique slug index. |

---

## 15. Open Questions

1. **Event scoping** — Should Humans support multiple concurrent events, or is it always one active event? This affects whether EventSettings is a singleton or a scoped entity.

2. **Ticket integration** — VIM integrates with Fistbump (Elsewhere's ticketing API). Does Humans need a generic ticket vendor integration, or is this out of scope for the shift management feature?

3. **MetaLead scoping mechanism** — Should `RoleAssignment` get a generic `ScopedEntityId` column for department-scoped roles, or should MetaLead scoping use a separate `MetaLeadAssignment` join table?

4. **Cantina export scope** — Is a CSV sufficient, or would a live dashboard showing "people on site today by dietary need" be more useful for kitchen operations?

---

## Glossary

| Term | Definition |
|------|-----------|
| **Bail** | When a volunteer cancels a confirmed signup, freeing the slot. Different from refusal (which is lead-initiated). Subject to early entry window restrictions. |
| **Build period** | The days/weeks before the main event when infrastructure is constructed (stages, bars, art installations). Volunteers working build arrive via early entry. |
| **Cantina** | The event kitchen that feeds volunteers. The cantina export provides daily dietary headcounts so the kitchen can plan meals and purchasing. |
| **Confirmed** | A signup that has been approved (or auto-approved for public-policy duties). The volunteer is expected to show up. |
| **Department** | An organizational grouping of related teams (e.g., "Bar" department contains "Main Bar", "Cocktail Bar", "Late Night Bar" teams). Scopes metalead authority and aggregates reporting. |
| **Duty** | Generic term for any piece of work a volunteer can sign up for — either a Shift or a Project. |
| **Early entry (EE)** | Permission to arrive on site before the event officially opens. Granted automatically to volunteers with confirmed build-period signups. The EE date is computed as their first shift start minus one day. |
| **EarlyEntryClose** | A datetime cutoff after which only leads/managers can modify early-entry signups. Prevents last-minute changes to the gate list. |
| **Enrolled** | Flag on a signup indicating the volunteer was assigned by a lead (voluntold), not self-signed-up. Enrolled volunteers can still bail. |
| **Essential / Important / Normal** | Priority levels for shifts and projects. Essential duties float to the top of the NoInfo urgency dashboard. Priority weights: Essential=6, Important=3, Normal=1. |
| **Event period** | The main event dates when the public is present. Shifts during this period are "event-time" duties. |
| **Fill rate** | The ratio of confirmed signups to needed slots (MaxVolunteers) for a duty, team, or department. Displayed on dashboards as a progress indicator. |
| **Lead** | A team's operational manager. In Humans, modeled via `TeamRoleDefinition` (Lead slot) + `TeamRoleAssignment`. Leads can create rotas/shifts, approve signups, and voluntell volunteers for their team. |
| **Manager** | A system-wide admin role with access to all teams, all exports, event settings, and migration tools. Higher authority than any lead. |
| **MetaLead** | A department-level lead who oversees all teams within their department. Can approve lead applications, view department-wide reports, and manage early entry for their department. |
| **NoInfo** | An operational coordination role for on-site gap-filling. NoInfo leads see urgency-ranked unfilled duties across all teams and can voluntell volunteers into any shift. Named after the "No Information" desk at Burning Man events — the team that handles everything that doesn't fit elsewhere. |
| **Pending** | A signup awaiting lead approval. Only exists for duties with `RequireApproval` policy. |
| **Policy** | A duty's signup mode — either `Public` (instant confirmation) or `RequireApproval` (lead must approve). Lead positions always require approval. |
| **Priority** | A duty's importance level (Normal, Important, Essential) that affects urgency scoring on the NoInfo dashboard and visual indicators throughout the system. |
| **Project** | A multi-day work commitment (typically build or strike). Unlike shifts, projects have date ranges instead of fixed hours, and per-day staffing targets that can vary (e.g., need 5 people Monday but 15 people Friday). Volunteers sign up for a sub-range within the project dates. |
| **Quirks** | Volunteer preferences or constraints (e.g., "sober shift", "work in shade", "night owl"). Matched against duty tags for urgency scoring. Similar to skills but more about work-style preferences. |
| **Refused** | A signup that a lead declined. The volunteer is notified. |
| **Rota** | A container that groups related shifts into a repeating pattern (e.g., "Bar Morning Shifts July 1-7"). Rotas have a date range, priority, and policy that child shifts inherit. In the UI, rotas appear as tabs in the lead dashboard. From the Latin *rota* meaning "wheel" — a rotating schedule. |
| **Shift** | A fixed-time work slot (e.g., "Bar shift 10:00–14:00 July 3"). Has exact start/end times, min/max volunteer counts, and belongs to a Rota. |
| **Skills** | Volunteer capabilities (e.g., "bartending", "first aid", "driving", "sound engineering"). Tagged on both volunteers and duties. Used by the urgency scoring algorithm to rank which open duties best match a given volunteer. |
| **Soft-delete** | Setting `IsActive = false` on a duty instead of removing it from the database. Hides it from volunteer browsing but preserves data and existing signups. |
| **Staffing** | Per-day volunteer targets for a Project, specifying how many people are needed on each day of the project. Can vary across days (ramp-up patterns are common during build). |
| **Strike period** | The days after the event when infrastructure is dismantled. The inverse of build. Has its own staffing needs and a separate NoInfo view. |
| **SystemOpenDate** | The datetime when the shift management system opens to regular volunteers for browsing and signup. Before this date, only leads and admins can access it. Replaces VIM's "FistOpenDate." |
| **Urgency score** | A computed ranking for unfilled duties on the NoInfo dashboard. Weighs priority level, remaining capacity (both min and max shortfall), shift duration, and skills/quirks match. Higher scores float to the top. |
| **Voluntell** | When a lead directly assigns a volunteer to a duty (portmanteau of "volunteer" + "tell"). Creates a confirmed signup with `Enrolled=true`. The volunteer is notified but can bail if they choose. Used by NoInfo leads for real-time gap-filling. |
