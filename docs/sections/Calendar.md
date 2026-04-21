# Calendar — Section Invariants

## Concepts

- **CalendarEvent** — a single scheduled event or recurring event series belonging to a team. Can be a one-time event or repeat according to an RFC 5545 recurrence rule.
- **CalendarEventException** — a per-occurrence override or cancellation for a recurring event. Allows changing title, time, or marking a specific occurrence as cancelled without deleting the entire series.

## Actors & Roles

| Actor | Capabilities |
|-------|---|
| Any authenticated human | View all calendar events (month and agenda views, filter by team) |
| Team Coordinator | Create, edit, delete events on their team. Cancel or override single occurrences of their team's recurring events |
| Admin | Full CRUD on any event. Cancel or override any occurrence of any event |

## Invariants

- Every `CalendarEvent` has a non-null `OwningTeamId` (foreign key to Teams)
- Only users with Admin role OR Coordinator role on the owning team may create, edit, or delete events, or manage exceptions (cancel/override occurrences)
- Title is required (non-null, non-empty)
- `StartUtc` is required
- `EndUtc` is required iff `IsAllDay = false`. When `IsAllDay = true`, `EndUtc` must be null
- `StartUtc <= EndUtc` when both are non-null
- `RecurrenceRule` and `RecurrenceTimezone` are set together, or neither is set (all-or-nothing invariant)
- `RecurrenceTimezone` defaults to `"Europe/Madrid"` if not specified on a recurring event
- `RecurrenceUntilUtc` is a denormalized copy of the RRULE's UNTIL clause, used only for indexable "does this recurrence reach my window" filtering during queries
- Soft-delete via `DeletedAt` — a global EF Core query filter hides deleted events from all queries
- `CalendarEventException` rows cascade-delete with the parent event (if the event is deleted, all exceptions are deleted)
- Unique index on `(EventId, OriginalOccurrenceStartUtc)` — prevents duplicate exceptions for the same occurrence
- Recurrence is expanded in-memory per-request against the event's `RecurrenceTimezone` using `Ical.Net` library (RFC 5545 compliant)

## Negative Access Rules

- Non-coordinators of the owning team **cannot** create, edit, or delete the event
- Non-coordinators **cannot** cancel or override specific occurrences
- Anonymous / unauthenticated visitors **cannot** access the calendar or view events (entire Calendar controller requires `[Authorize]`)

## Triggers

None in v1. No automatic notifications or cross-section audit rows beyond standard `CreatedAt` / `UpdatedAt` / `CreatedByUserId` timestamps.

## Cross-Section Dependencies

- **Teams** — every event is owned by exactly one team; coordinator status on the owning team gates edit/delete/exception permissions
- **Users** — `CreatedByUserId` for audit trail; used in authorization checks via `User.IsInRole()` and coordinator lookup
