# Community Calendar — Design

**Issue:** [#513](https://github.com/nobodies-collective/Humans/issues/513)
**Section:** Calendar (new)
**Status:** Design; awaiting review.
**Date:** 2026-04-21

## Context

Most sections of the app carry dates — camp placement windows, shift signup open dates, budget submission deadlines, ticket sale boundaries, Board assemblies — but those dates live inside each module's UI. There is no single place for the community or coordinators to see "what is happening when." On top of module-sourced dates, coordinators and teams want to schedule their own events (community calls, recurring coordinator meetings, department syncs) and have the community see them in one place.

This design covers the full feature shape, but scopes v1 tightly. Module aggregation, public views, iCal export, and personalized "my calendar" views are explicitly post-v1 and tracked in follow-up slices.

Issue #161 (personal shift iCal feed) is **out of scope** for this design. It is a different feature (personal, per-user feed of shifts) and is not superseded by the community calendar. The two may converge later via a personal feed subscribing to the community calendar as one of its sources; no dependency is introduced in either direction.

## Goals

- Give the community a single canonical place to see upcoming events.
- Give coordinators a simple way to publish events for their team or for cross-cutting groups.
- Handle recurring events correctly, including across DST boundaries.
- Lay a data model that post-v1 slices (module aggregation, audience scoping, iCal export) can build on without migration pain.

## Non-goals (v1)

- Module-sourced events (Camps, Shifts, Budget, etc.).
- Public / unauthenticated view.
- Audience scoping (team-only vs. org-wide visibility).
- iCal feed export.
- Notifications / reminders.
- RSVP / attendance / capacity.
- "My calendar" personalized view.
- Drag-and-drop rescheduling.
- External calendar two-way sync.

## v1 Shape

- A new top-level **Calendar** section with month and agenda views, visible to all logged-in humans.
- Standalone events only — no aggregation from other sections in v1.
- Single events and recurring events. Recurrence expressed as [RFC 5545](https://datatracker.ietf.org/doc/html/rfc5545) RRULE strings and expanded with [Ical.Net](https://github.com/rianjs/ical.net) (MIT).
- Every event has a required owning `Team` (FK). Coordinators of the owning team plus Admin can create / edit / delete. All other logged-in humans can view.
- Cross-cutting events use existing system teams (Volunteers, Coordinators, Board, Asociados, Colaboradors, BarrioLeads — see `src/Humans.Domain/Enums/SystemTeamType.cs`). No new team scaffolding needed.
- Times stored as `Instant` (UTC). Recurring events additionally store an IANA `RecurrenceTimezone` (default `Europe/Madrid`) so RRULE expansion preserves local wall-clock time across DST flips. The calendar renders occurrences in the viewer's browser timezone and shows a visible timezone label (e.g. "All times shown in Europe/Madrid").

## Post-v1 Slicing

Each becomes its own follow-up issue once this design lands:

- **Slice 2 — Module aggregation.** Each section exposes an `ICalendarContributor` that emits events for a given time window. Calendar queries fan out to contributors and merge with stored events. Contributors identified in v1: Shifts (signup open, signup close, event start), Camps (placement open, placement close), Budget (submission deadlines), Tickets (sale open / close), Governance (assembly dates, elections), Onboarding (cohort milestones if any), Campaigns (send dates), Legal (document effective / review dates).
- **Slice 3 — Audience scoping + team-only events.** Add a visibility flag (`TeamOnly` vs. `AllHumans`). Team-only events are visible only to members of the owning team. Module-sourced events inherit visibility from their source.
- **Slice 4 — Public view + iCal feed.** Public (unauthenticated) view of events flagged `Public`. iCal feed per audience scope at a URL pattern TBD in that slice. Defer the #161 convergence question until this slice.
- **Slice 5 — "My calendar" + notifications.** Personalized view filtered to events relevant to the current human (their teams, their camps, their shifts). Optional reminder notifications via the existing notification inbox.
- **Slice 6 (maybe) — RSVP.** If/when desired; explicitly out of scope today.

## Data Model

Two new entities, in `Humans.Domain/Entities/Calendar/`.

### `CalendarEvent`

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `Title` | `string` | Required, max 200 |
| `Description` | `string?` | Plain text, max 4000 (no markdown in v1) |
| `Location` | `string?` | Free text, max 500. "Casa del Pueblo", "Zoom", etc. |
| `LocationUrl` | `string?` | Optional URL. Rendered as a link alongside `Location` if present. |
| `OwningTeamId` | `Guid` | FK → `Team.Id`, required. Restrict delete (team with events cannot be deleted). |
| `StartUtc` | `Instant` | Required. First occurrence's start. |
| `EndUtc` | `Instant?` | Null iff `IsAllDay`; otherwise required. |
| `IsAllDay` | `bool` | Default false. |
| `RecurrenceRule` | `string?` | RFC 5545 RRULE. Null = single event. |
| `RecurrenceTimezone` | `string?` | IANA zone (e.g. `Europe/Madrid`). Required iff `RecurrenceRule` set. |
| `RecurrenceUntilUtc` | `Instant?` | Denormalized copy of RRULE's UNTIL (or null for open-ended rules); enables indexable "does this recur into window" queries without parsing RRULE at the DB layer. |
| `CreatedByUserId` | `Guid` | FK → `User.Id` |
| `CreatedAt` | `Instant` | |
| `UpdatedAt` | `Instant` | |
| `DeletedAt` | `Instant?` | Soft delete. |

**Indexes:**

- `(OwningTeamId, StartUtc)` — team sub-calendar queries.
- `(StartUtc, RecurrenceUntilUtc)` — "events in window" filtering.
- Partial index `WHERE DeletedAt IS NULL` on both, if PostgreSQL supports it in the current EF configuration; otherwise rely on query filter.

**Invariants (enforced in domain + EF configuration):**

- `EndUtc IS NOT NULL` when `IsAllDay = false` (timed events always have an end). When `IsAllDay = true`, `EndUtc` is optional — null for a single-day all-day event, set for a multi-day all-day range.
- `RecurrenceTimezone IS NOT NULL` iff `RecurrenceRule IS NOT NULL`.
- `RecurrenceUntilUtc` is the last instant the recurrence can possibly contribute an occurrence (RRULE `UNTIL` if present, else the end of the `COUNT`-th occurrence, else null for open-ended rules); enforced by service on write and used for SQL prefiltering.
- `StartUtc <= EndUtc` when `EndUtc` is present.

### `CalendarEventException`

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `EventId` | `Guid` | FK → `CalendarEvent.Id`, cascade delete. |
| `OriginalOccurrenceStartUtc` | `Instant` | Identifies which occurrence this exception targets. |
| `IsCancelled` | `bool` | Default false. If true, this occurrence is skipped. |
| `OverrideStartUtc` | `Instant?` | If set, this occurrence moves here. |
| `OverrideEndUtc` | `Instant?` | |
| `OverrideTitle` | `string?` | |
| `OverrideDescription` | `string?` | |
| `OverrideLocation` | `string?` | |
| `OverrideLocationUrl` | `string?` | |
| `CreatedByUserId` | `Guid` | |
| `CreatedAt` | `Instant` | |
| `UpdatedAt` | `Instant` | |

**Unique constraint:** `(EventId, OriginalOccurrenceStartUtc)` — one exception per original occurrence.

**Invariant:** `IsCancelled = true` OR at least one `Override*` field is non-null. An exception with neither cancel nor overrides is meaningless; service write path rejects it.

## Service Layer

**`ICalendarService`** — new interface in `Humans.Application/Interfaces/`, implementation in `Humans.Infrastructure/Services/CalendarService.cs`. Per `docs/architecture/design-rules.md`, this service owns the two tables exclusively; no other service queries them directly.

```csharp
public interface ICalendarService
{
    Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesInWindowAsync(
        Instant from, Instant to, Guid? teamId = null, CancellationToken ct = default);

    Task<CalendarEvent?> GetEventByIdAsync(Guid id, CancellationToken ct = default);

    Task<CalendarEvent> CreateEventAsync(CreateCalendarEventDto dto, Guid createdByUserId, CancellationToken ct);
    Task<CalendarEvent> UpdateEventAsync(Guid id, UpdateCalendarEventDto dto, Guid updatedByUserId, CancellationToken ct);
    Task DeleteEventAsync(Guid id, Guid deletedByUserId, CancellationToken ct); // soft delete

    Task CancelOccurrenceAsync(Guid eventId, Instant originalStartUtc, Guid userId, CancellationToken ct);
    Task OverrideOccurrenceAsync(Guid eventId, Instant originalStartUtc, OverrideOccurrenceDto dto, Guid userId, CancellationToken ct);
}
```

### DTOs

`CreateCalendarEventDto`, `UpdateCalendarEventDto`, `OverrideOccurrenceDto` — field-for-field mirrors of the relevant entity fields, minus audit columns.

`CalendarOccurrence` — a transient DTO representing a concrete expanded occurrence (not persisted):

```csharp
public sealed record CalendarOccurrence(
    Guid EventId,
    Instant OccurrenceStartUtc,
    Instant? OccurrenceEndUtc,
    bool IsAllDay,
    string Title,
    string? Description,
    string? Location,
    string? LocationUrl,
    Guid OwningTeamId,
    string OwningTeamName,
    bool IsRecurring,
    Instant? OriginalOccurrenceStartUtc);  // null for non-recurring; non-null for recurring (needed to target exceptions)
```

### Occurrence expansion (`GetOccurrencesInWindowAsync`)

1. **Candidate filter (SQL):**
   ```
   WHERE DeletedAt IS NULL
     AND (OwningTeamId = @teamId OR @teamId IS NULL)
     AND StartUtc <= @to
     AND (RecurrenceUntilUtc IS NULL OR RecurrenceUntilUtc >= @from)
   ```
2. **Expand each candidate in memory:**
   - If `RecurrenceRule` is null: emit a single occurrence iff `[StartUtc, EndUtc]` overlaps `[from, to]`.
   - If `RecurrenceRule` is set: construct an `Ical.Net` `CalendarEvent` with DTSTART in `RecurrenceTimezone` equal to the event's `StartUtc` converted to that zone; attach the RRULE; call `GetOccurrences(fromLocal, toLocal)` to get occurrences, then convert back to `Instant`.
3. **Load exceptions** for the candidate event IDs with a single `WHERE EventId IN (…)` query. Key by `(EventId, OriginalOccurrenceStartUtc)`.
4. **Apply exceptions:**
   - `IsCancelled = true` → drop the occurrence.
   - Overrides → replace corresponding fields on the emitted DTO.
5. **Sort** by `OccurrenceStartUtc` ascending.

**Caching:** `IMemoryCache` holds the full list of active (non-deleted) `CalendarEvent`s keyed by a singleton cache entry, invalidated on any create / update / delete. At ~500 users and expected event counts (low hundreds over any year), this is trivially sized. The expansion itself is recomputed per request against a sliding window; it is fast and does not need its own cache layer. Matches the service-owns-its-cache rule.

### Authorization

Authorization lives in controllers per `design-rules.md`. A new resource-based handler:

```csharp
public sealed class CalendarEditorAuthorizationHandler
    : AuthorizationHandler<CalendarEditorRequirement, Team>
{
    // succeeds if user is Admin OR is a coordinator of the owning team
}
```

Registered against a policy named `"CalendarEditor"`. Controller calls `IAuthorizationService.AuthorizeAsync(User, owningTeam, "CalendarEditor")` before mutating. No `isPrivileged` boolean threaded through the service; the service trusts the controller to have authorized the caller.

## Controller & Routes

**`CalendarController`** in `Humans.Web/Controllers/`.

| Method | Route | Purpose |
|---|---|---|
| GET | `/Calendar` | Month view (default: current month) |
| GET | `/Calendar/Agenda` | Agenda view (default: today + 60 days) |
| GET | `/Calendar/Team/{teamId}` | Team-filtered month / agenda |
| GET | `/Calendar/Event/{id}` | Event detail |
| GET | `/Calendar/Event/Create?teamId={guid}` | Create form |
| POST | `/Calendar/Event/Create` | Create |
| GET | `/Calendar/Event/{id}/Edit` | Edit form |
| POST | `/Calendar/Event/{id}/Edit` | Update |
| POST | `/Calendar/Event/{id}/Delete` | Soft delete |
| POST | `/Calendar/Event/{id}/Occurrence/{originalStartUtc}/Cancel` | Cancel a single recurring occurrence |
| GET | `/Calendar/Event/{id}/Occurrence/{originalStartUtc}/Edit` | Override form for a single recurring occurrence |
| POST | `/Calendar/Event/{id}/Occurrence/{originalStartUtc}/Edit` | Save override |

`{originalStartUtc}` is serialized as an ISO-8601 UTC string in the URL.

## Views

### Month view (`/Calendar`)

- Standard month grid (7 columns × 5–6 rows). Prev / next month navigation; a "Today" button.
- Each day cell shows up to 3 event chips plus a "… N more" link that expands into an agenda view filtered to that day.
- Event chip: colored dot (team color if present; neutral grey otherwise), title, start time (e.g. `19:00`), truncated. Hover reveals full title + location. Click opens the event detail.
- All-day events render as full-width bars across their day range.
- Recurring occurrences look identical to single events; no special badge.
- Header: current month label + viewer's browser timezone label ("All times shown in Europe/Madrid").
- Filter bar: "All teams" (default), team multiselect dropdown.

### Agenda view (`/Calendar/Agenda`)

- Flat chronological list grouped by date.
- Each entry: date, time (or "All day"), title, owning team (linked), location (linked if URL), short description snippet.
- Default window: today + 60 days. Optional `?from=YYYY-MM-DD&to=YYYY-MM-DD`.

### Team sub-calendar (`/Calendar/Team/{teamId}`)

- Month and agenda views, pre-filtered to the owning team. Reachable from the team page via a new "Team calendar" link.

### Event detail (`/Calendar/Event/{id}`)

- Title, owning team (linked), description, location (linked if URL present), start / end with TZ label, recurrence summary rendered in human-readable form ("Every other Tuesday until 2026-05-26"), list of next 5 upcoming occurrences.
- For each upcoming occurrence (recurring events only): per-occurrence "Cancel this occurrence" and "Edit this occurrence" buttons, visible only to authorized users.
- Top-right: "Edit event" and "Delete event" buttons, visible only to authorized users.

### Create / edit form

- Fields: title, description, location, optional location URL, owning team dropdown (filtered to teams the user coordinates, unless Admin → all teams), all-day checkbox, start, end, "Repeats" toggle → recurrence builder.
- Recurrence builder is intentionally simple: frequency (daily / weekly / monthly / yearly), interval ("every N"), for weekly → weekday multiselect, end condition (never / until date / after N occurrences). Under the hood assembles an RRULE. Raw RRULE textbox available to Admin only for edge cases.
- Recurrence timezone dropdown (IANA zones from NodaTime's tz db) defaults to `Europe/Madrid`.

### Nav

Per `CLAUDE.md` — every new page gets a nav link.

- New top-level **Calendar** item in the main nav (between Teams and wherever makes sense in the existing order; to be finalized during implementation, but the link must exist).
- Team detail page gains a "Team calendar" link to `/Calendar/Team/{teamId}`.
- Create-event entrypoint lives on `/Calendar` itself (a "New event" button visible to any coordinator or Admin; the form's team dropdown is filtered by authorization).

## Testing

Per existing testing conventions (integration tests hit the real DB; no mocking of EF):

**Domain:**

- `CalendarEvent` invariants: `EndUtc` required unless all-day; `RecurrenceTimezone` required iff `RecurrenceRule` set.
- `CalendarEventException` invariant: must either cancel or override something.

**Service (integration):**

- Single non-recurring event: present in window when overlapping, absent when outside.
- Recurring weekly event across a Spain DST boundary (late March or late October): occurrences stay at the expected local time, not drifted by an hour.
- Exception `IsCancelled = true`: occurrence absent from window expansion.
- Exception with `OverrideStartUtc`: occurrence moved in window expansion.
- Exception with `OverrideTitle`: occurrence carries the override title.
- Soft-deleted event: absent from all queries.
- `GetOccurrencesInWindowAsync(teamId: X)`: only returns events where `OwningTeamId = X`.

**Authorization:**

- Coordinator of Team A receives 403 attempting to edit a Team B event.
- Coordinator of Team A succeeds editing a Team A event.
- Admin succeeds editing any event.
- Non-coordinator logged-in human receives 403 on any create / edit / delete.

**Controller (integration):**

- Create → event appears in `GetOccurrencesInWindowAsync`.
- Edit round trip: fields persist.
- Delete → event absent from subsequent queries.
- Per-occurrence cancel: one occurrence skipped, others present.
- Per-occurrence override: one occurrence moved, others unchanged.

**E2E smoke:**

- A new section-based smoke test that logs in, opens `/Calendar`, verifies the current month page renders without error.

## Migration & Rollout

- **EF migration:** one migration creates `calendar_events` and `calendar_event_exceptions`. Run the EF migration reviewer agent per `CLAUDE.md` before commit.
- **Seeding:** none. Calendar ships empty; coordinators populate it.
- **Feature flag:** none. The nav link appears on deploy; an empty calendar is self-evident.
- **Section invariant doc:** add `docs/sections/Calendar.md` covering actors (coordinators of the owning team, Admin, humans as viewers), invariants (every event has an owning team; only owning-team coordinators plus Admin can mutate; exceptions cascade-delete with the parent; `RecurrenceTimezone` required iff `RecurrenceRule`), and triggers (none in v1).
- **Feature spec:** add `docs/features/39-community-calendar.md` mirroring the v1 surface and noting the post-v1 slices.
- **NuGet:** add `Ical.Net` (MIT). Update `About` page package list per `CLAUDE.md`.

## Dependencies

- `Ical.Net` — MIT-licensed. Used for RRULE expansion against IANA timezones. Actively maintained; handles DST, leap days, BYDAY / BYMONTHDAY / BYSETPOS, COUNT, UNTIL.
- Existing: NodaTime (already in stack), EF Core, Microsoft.AspNetCore.Authorization.

## Open Questions

None blocking v1. The following are deferred to their respective follow-up slices:

- Exact color coding for team chips on the month view — may or may not require a `Color` column on `Team`. Slice 2 or 3 decision.
- Exact RRULE-expansion semantics when an event's `StartUtc` is moved after it already has exceptions tied to prior occurrences — addressed during implementation; current plan is to preserve exceptions by their `OriginalOccurrenceStartUtc` and let consumers see that an exception may no longer match a valid occurrence (surfaced as a stale-exception warning in the edit UI). Can be revisited if it feels wrong in practice.
- Whether the public view is Slice 3 or Slice 4 — ordering decision at follow-up-issue time.

## Relation to Other Issues

- **#161 (shift iCal feed).** Independent. Different feature (personal feed of a user's shifts vs. community-wide calendar). No merge, no supersession; the two may converge in a later slice where a personal feed subscribes to the community calendar as one of its sources.
- **#513 (this).** This design closes the design-required portion of #513. Implementation issues will be opened per slice.
