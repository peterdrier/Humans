<!-- freshness:triggers
  src/Humans.Application/Services/Calendar/**
  src/Humans.Web/Controllers/CalendarController.cs
  src/Humans.Domain/Entities/CalendarEvent.cs
  src/Humans.Domain/Entities/CalendarEventException.cs
  src/Humans.Infrastructure/Data/Configurations/Calendar/**
-->
<!-- freshness:flag-on-change
  Calendar entities, recurrence/timezone handling, audit-log-based accountability, or v1 scope boundary may have shifted.
-->

# Community Calendar

## Business Context

Nobodies Collective teams coordinate through meetings, workshops, and gatherings. Currently, events are scattered across team Drive folders and chat messages, making it hard for the whole community to discover what's happening. A centralized calendar gives all authenticated humans visibility into team-organized events and helps coordinators and admin see the full calendar landscape across the organization.

## Scope — Slice 1 (v1)

- Month view and agenda (upcoming events) view
- Team-filtered view — show only events from selected team(s)
- Single and recurring events (RFC 5545 recurrence rules)
- Cancel or override individual occurrences without deleting the entire series
- Team-owned events; any authenticated human can create, edit, or delete events for any team
- Changes captured in the audit log (who / when / what) rather than gated by upfront authorization
- NodaTime and IANA timezone-aware recurrence expansion (occurrences expand in their configured timezone and render in the viewer's browser timezone)

## Out of Scope — Post-v1 Slices

The following are explicitly deferred to future slices:

- **Module aggregation** (Shifts contributing shift events, Camps contributing camp dates, Budget contributing budget review events, etc. via `ICalendarContributor` interface)
- **Audience scoping** (private events, visibility rules per team)
- **iCal export feed** (`.ics` for subscriptions)
- **Public view** (anonymous/unauthenticated calendar)
- **Personal calendar digest and notifications** ("your upcoming events" email)
- **RSVP and attendance tracking**
- **Event categories, colors, custom fields**

See the design document at `docs/superpowers/specs/2026-04-21-community-calendar-design.md` for detailed design rationale and v2+ planning.

## User Stories

### US-39.1: Browse Community Calendar (Month View)
**As an** authenticated human
**I want to** view the community calendar in a month view
**So that** I can see what events are happening across all teams

**Acceptance Criteria:**
- Calendar grid shows a full month with events listed on their dates
- Events show team name, title, and start time
- Click event to see full details (description, time range, recurrence info)
- Month navigation (prev/next, goto date)
- Today is visually highlighted

### US-39.2: Filter Calendar by Team
**As an** authenticated human
**I want to** filter the calendar to show events from specific teams
**So that** I can focus on teams I care about

**Acceptance Criteria:**
- Team filter dropdown or multi-select widget
- Can select one or more teams
- "All Teams" option shows all events
- Filter persists in the session or browser storage
- Filtered view updates immediately

### US-39.3: Create Team Event
**As an** authenticated human
**I want to** create an event on any team's calendar
**So that** the whole community knows about a meeting or workshop

**Acceptance Criteria:**
- Form with: title, description (optional), team (pre-selected if viewing team), date/time, timezone
- Support single event or recurring (select recurrence frequency, interval, until date)
- Save creates the event and redirects to event details
- Required fields: title, team, start date/time
- If recurring, show preview of next 5 occurrences

### US-39.4: Edit or Delete Team Event
**As an** authenticated human
**I want to** edit or delete an event on any team's calendar
**So that** I can fix mistakes or remove cancelled events

**Acceptance Criteria:**
- Edit button visible on every event detail page (no role gate)
- Edit form prefilled with current values
- Can change title, description, date/time, recurrence, owning team
- Delete button with confirmation
- After save/delete, redirect to calendar view
- Every change recorded in the audit log (actor + timestamp)

### US-39.5: Cancel or Override Event Occurrence
**As an** authenticated human
**I want to** cancel a single occurrence of a recurring event or change its time without affecting the whole series
**So that** I can reschedule or cancel a single meeting without deleting all future occurrences

**Acceptance Criteria:**
- On a recurring event, show "Manage Occurrences" or similar UI
- List next 10 upcoming occurrences
- Per-occurrence actions: cancel, reschedule (change time/title)
- Cancelled occurrence is hidden from calendar view
- Rescheduled occurrence shows new time; recurrence rule unchanged
- Changes recorded in `CalendarEventException` table

## Data Model

### CalendarEvent
```
CalendarEvent
├── Id: Guid
├── OwningTeamId: Guid (FK → Team)
├── CreatedByUserId: Guid (FK → User)
├── Title: string (required, 256)
├── Description: string? (4000)
├── StartUtc: Instant (required)
├── EndUtc: Instant? (required iff IsAllDay = false)
├── IsAllDay: bool (default false)
├── RecurrenceRule: string? (RFC 5545 RRULE, e.g., "FREQ=WEEKLY;BYDAY=MO")
├── RecurrenceTimezone: string? (IANA timezone, e.g., "Europe/Madrid")
├── RecurrenceUntilUtc: Instant? (denormalized UNTIL from RRULE, for indexing)
├── CreatedAt: Instant
├── UpdatedAt: Instant
├── DeletedAt: Instant? (soft-delete)
└── Navigation: OwningTeam, CreatedByUser, Exceptions
```

### CalendarEventException
```
CalendarEventException
├── Id: Guid
├── EventId: Guid (FK → CalendarEvent, cascade-delete)
├── OriginalOccurrenceStartUtc: Instant (the occurrence being modified)
├── Title: string? (override title, null = use parent event title)
├── StartUtc: Instant? (override start time, null = use recurrence result)
├── EndUtc: Instant? (override end time, null = use recurrence result)
├── IsCancelled: bool (true = skip this occurrence in calendar view)
├── CreatedAt: Instant
├── UpdatedAt: Instant
└── Navigation: Event
```

## Authorization

The calendar is intentionally open: any authenticated human can create, edit, delete, cancel, or override any event. The controller requires `[Authorize]` (authentication only, no role or resource gate). Accountability lives in the audit log:

- Every mutation (create, update, delete, occurrence cancel, occurrence override) writes an `AuditLogEntry` via `IAuditLogService` with the actor's user ID.
- The event detail page renders the full audit history via the shared `AuditLog` view component, with an inline "Created {date} · Last updated {date}" line above it.
- The owning team is recorded on each audit entry as `relatedEntityId` so audit queries can filter by team.

No resource-based authorization handler, no `CalendarEditor` policy. If upfront gates become needed later (e.g., to restrict deletion), add them via the standard `IAuthorizationService` pattern used by Teams / Camps / Budget.

## Timezones & DST

Every recurring event is tied to an IANA timezone (e.g., `"Europe/Madrid"`, `"Europe/Berlin"`). When expanding occurrences (e.g., a weekly 19:00 meeting):

1. Recurrence rule is expanded in the event's configured timezone using `Ical.Net`
2. Each occurrence start/end is calculated in that timezone, respecting DST transitions
3. Occurrences are stored/queried in UTC (`StartUtc`, `EndUtc`)
4. When rendering to the user's browser, occurrences are converted to their local timezone (via JavaScript or `NodaTime` if server-rendered)

This ensures a recurring "19:00 weekly on Monday" stays at 19:00 local time even when daylight saving changes occur.

## Related Features

- [Teams & Working Groups](06-teams.md) — Calendar events are owned by teams; the team reference is recorded on every audit entry for team-scoped audit filtering
