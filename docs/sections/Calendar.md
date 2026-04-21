# Calendar — Section Invariants

## Concepts

- **CalendarEvent** — a single scheduled event or recurring event series belonging to a team. Can be a one-time event or repeat according to an RFC 5545 recurrence rule.
- **CalendarEventException** — a per-occurrence override or cancellation for a recurring event. Allows changing title, time, or marking a specific occurrence as cancelled without deleting the entire series.

## Actors & Roles

| Actor | Capabilities |
|-------|---|
| Any authenticated human | View all calendar events (grid, list, agenda views, filter by team). Create, edit, delete events on any team. Cancel or override single occurrences of recurring events. All changes recorded in the audit log. |
| Admin | Same as any authenticated human. No additional calendar-specific privileges in v1. |

The calendar is intentionally open: no resource-based authorization gates edit/delete/cancel. Accountability is via the audit log (`IAuditLogService`), which records who performed each mutation.

## Invariants

- Every `CalendarEvent` has a non-null `OwningTeamId` (foreign key to Teams)
- Only authenticated humans may create, edit, or delete events, or manage exceptions (enforced by `[Authorize]` on `CalendarController`)
- Every mutating action (create / update / delete / cancel-occurrence / override-occurrence) writes an `AuditLogEntry` with the actor's user ID
- Title is required (non-null, non-empty)
- `StartUtc` is required
- `EndUtc` is required for timed events (`IsAllDay = false`). For all-day events it is optional — null means a single-day event, set means a multi-day all-day range
- `StartUtc <= EndUtc` when both are non-null
- `RecurrenceRule` and `RecurrenceTimezone` are set together, or neither is set (all-or-nothing invariant)
- `RecurrenceTimezone` defaults to `"Europe/Madrid"` if not specified on a recurring event
- `RecurrenceUntilUtc` is the last instant the recurrence can possibly produce an occurrence (RRULE `UNTIL` if present, else the end of the `COUNT`-th occurrence computed via Ical.Net, else null for open-ended rules); used for indexable SQL window prefiltering
- Soft-delete via `DeletedAt` — a global EF Core query filter hides deleted events from all queries
- `CalendarEventException` rows cascade-delete with the parent event (if the event is deleted, all exceptions are deleted)
- Unique index on `(EventId, OriginalOccurrenceStartUtc)` — prevents duplicate exceptions for the same occurrence
- Recurrence is expanded in-memory per-request against the event's `RecurrenceTimezone` using `Ical.Net` library (RFC 5545 compliant)

## Negative Access Rules

- Anonymous / unauthenticated visitors **cannot** access the calendar or view events (entire `CalendarController` requires `[Authorize]`)

## Triggers

- Every mutation writes an `AuditLogEntry` via `IAuditLogService` (`CalendarEventCreated`, `CalendarEventUpdated`, `CalendarEventDeleted`, `CalendarOccurrenceCancelled`, `CalendarOccurrenceOverridden`), persisted atomically with the underlying change.

## Cross-Section Dependencies

- **Teams** — every event is owned by exactly one team; the team is referenced in the audit entry as `relatedEntityId` for team-scoped audit filtering
- **Users** — `CreatedByUserId` is persisted on the entity; every subsequent mutation logs the actor via the audit log (no `UpdatedByUserId` column)
