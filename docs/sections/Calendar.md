# Calendar — Section Invariants

Community calendar: one-off and recurring events per team, with per-occurrence overrides/cancellations.

## Concepts

- **CalendarEvent** — a single scheduled event or recurring event series belonging to a team. Can be a one-time event or repeat according to an RFC 5545 recurrence rule.
- **CalendarEventException** — a per-occurrence override or cancellation for a recurring event. Allows changing title, time, or marking a specific occurrence as cancelled without deleting the entire series.

## Data Model

### CalendarEvent

A community-calendar event belonging to a team. May be a single event or a recurring series defined by an RFC 5545 `RRULE` expanded against an IANA timezone (default `Europe/Madrid`). Soft-deleted via `DeletedAt` with a global EF query filter.

**Table:** `calendar_events`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Title | string (200) | Required |
| Description | string (4000) | Optional |
| Location | string (500) | Optional |
| LocationUrl | string (2000) | Optional |
| OwningTeamId | Guid | FK → Team (`OnDelete: Restrict`) — **FK only**, no nav |
| StartUtc | Instant | First (or only) occurrence start in UTC |
| EndUtc | Instant? | Required iff `IsAllDay = false`. For all-day events, set to half-open exclusive midnight (`EndDate + 1 day` 00:00 in `RecurrenceTimezone`). May be null on legacy single-day all-day rows |
| IsAllDay | bool | All-day event |
| RecurrenceRule | string (500)? | RFC 5545 RRULE (no `RRULE:` prefix). Null = single event |
| RecurrenceTimezone | string (100)? | IANA TZ. Required iff `RecurrenceRule` is set |
| RecurrenceUntilUtc | Instant? | Denormalised UNTIL — supports indexable "rule reaches window" queries |
| CreatedByUserId | Guid | FK → User — **FK only**, no nav |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |
| DeletedAt | Instant? | Soft delete; global query filter excludes non-null |

**Indexes:** `(OwningTeamId, StartUtc)`, `(StartUtc, RecurrenceUntilUtc)`.

**Aggregate-local navs:** `CalendarEvent.Exceptions`.

### CalendarEventException

Per-occurrence override or cancellation for a recurring `CalendarEvent`. Cascade-deletes with the parent event.

**Table:** `calendar_event_exceptions`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| EventId | Guid | FK → CalendarEvent (`OnDelete: Cascade`) |
| OriginalOccurrenceStartUtc | Instant | The unmodified start of the occurrence this exception targets |
| IsCancelled | bool | If true, occurrence is dropped during expansion |
| OverrideStartUtc | Instant? | |
| OverrideEndUtc | Instant? | |
| OverrideTitle | string (200)? | |
| OverrideDescription | string (4000)? | |
| OverrideLocation | string (500)? | |
| OverrideLocationUrl | string (2000)? | |
| CreatedByUserId | Guid | FK → User — **FK only**, no nav |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Indexes:** unique `(EventId, OriginalOccurrenceStartUtc)` — one exception per (event, occurrence).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any authenticated human | View all calendar events (grid, list, agenda views, filter by team). Create, edit, delete events on any team. Cancel or override single occurrences of recurring events. All changes recorded in the audit log |
| Admin | Same as any authenticated human. No additional calendar-specific privileges in v1 |

The calendar is intentionally open: no resource-based authorization gates edit/delete/cancel. Accountability is via the audit log (`IAuditLogService`), which records who performed each mutation.

## Invariants

- Every `CalendarEvent` has a non-null `OwningTeamId` (foreign key to Teams).
- Only authenticated humans may create, edit, or delete events, or manage exceptions (enforced by `[Authorize]` on `CalendarController`).
- Every mutating action (create / update / delete / cancel-occurrence / override-occurrence) writes an `AuditLogEntry` with the actor's user ID.
- Title is required (non-null, non-empty).
- `StartUtc` is required.
- `EndUtc` is required for timed events (`IsAllDay = false`). For all-day events created or edited via the calendar form, `EndUtc` is set to half-open exclusive midnight (`StartDate.PlusDays(InclusiveDays).AtMidnight()` in `RecurrenceTimezone`); the display layer recovers the inclusive end date by subtracting one tick before projecting to local. Legacy all-day rows may still have null `EndUtc` (treated as single-day).
- `StartUtc <= EndUtc` when both are non-null.
- `RecurrenceRule` and `RecurrenceTimezone` are set together, or neither is set (all-or-nothing invariant).
- `RecurrenceTimezone` defaults to `"Europe/Madrid"` if not specified on a recurring event.
- `RecurrenceUntilUtc` is the last instant the recurrence can possibly produce an occurrence (RRULE `UNTIL` if present, else the end of the `COUNT`-th occurrence computed via Ical.Net, else null for open-ended rules); used for indexable SQL window prefiltering.
- Soft-delete via `DeletedAt` — a global EF Core query filter hides deleted events from all queries.
- `CalendarEventException` rows cascade-delete with the parent event.
- Unique index on `(EventId, OriginalOccurrenceStartUtc)` — prevents duplicate exceptions for the same occurrence.
- Recurrence is expanded in-memory per-request against the event's `RecurrenceTimezone` using `Ical.Net` library (RFC 5545 compliant).

## Negative Access Rules

- Anonymous / unauthenticated visitors **cannot** access the calendar or view events (entire `CalendarController` requires `[Authorize]`).

## Triggers

- Every mutation writes an `AuditLogEntry` via `IAuditLogService` (`CalendarEventCreated`, `CalendarEventUpdated`, `CalendarEventDeleted`, `CalendarOccurrenceCancelled`, `CalendarOccurrenceOverridden`), persisted atomically with the underlying change.

## Cross-Section Dependencies

- **Teams:** `ITeamService` — every event is owned by exactly one team; the team is referenced in the audit entry as `relatedEntityId` for team-scoped audit filtering.
- **Users/Identity:** `CreatedByUserId` is persisted on the entity; every subsequent mutation logs the actor via the audit log (no `UpdatedByUserId` column).
- **Audit Log:** `IAuditLogService` — every mutation writes an entry.

## Architecture

**Owning services:** `CalendarService`
**Owned tables:** `calendar_events`, `calendar_event_exceptions`
**Status:** (C) Pre-migration — `CalendarService` is in `Humans.Infrastructure/Services/CalendarService.cs` and injects `HumansDbContext` directly. Not yet listed in design-rules §15i.

> **Status (pre-migration):** This section currently follows the "services in Infrastructure, direct DbContext" model. It will be migrated to the §15 repository pattern per [`../architecture/design-rules.md`](../architecture/design-rules.md). **Delete this block once the migration lands and this section's services live in `Humans.Application` with `*Repository.cs` impls in `Humans.Infrastructure/Repositories/`.**

### Target repositories

- **`ICalendarRepository`** — owns `calendar_events`, `calendar_event_exceptions`
  - Aggregate-local navs kept: `CalendarEvent.Exceptions`
  - Cross-domain navs stripped: `OwningTeamId` stays FK only; any future display of owning team name routes through `ITeamService.GetTeamNamesByIdsAsync`.
  - Soft-delete filter: repository must include a method that bypasses the `DeletedAt` filter for admin restore / audit workflows if/when they are added.

### Current violations

No drift tracked yet — migration has not begun. File the current-state audit when opening the migration issue.

### Touch-and-clean guidance

- When adding new controller actions, route through `ICalendarService` — do not inject `HumansDbContext` into `CalendarController`.
- Do not add `.Include(e => e.OwningTeam)` or `.Include(e => e.CreatedByUser)` — the entity carries FKs only and will stay that way post-migration.
- Every new mutation must write an `AuditLogEntry` via `IAuditLogService`; do not skip audit for "admin convenience" operations.
- Every new page must have a nav link (CLAUDE.md coding rules — no orphan pages).
