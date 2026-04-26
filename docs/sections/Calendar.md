<!-- freshness:triggers
  src/Humans.Application/Services/Calendar/**
  src/Humans.Infrastructure/Services/CalendarService.cs
  src/Humans.Domain/Entities/CalendarEvent.cs
  src/Humans.Domain/Entities/CalendarEventException.cs
  src/Humans.Infrastructure/Data/Configurations/Calendar/**
  src/Humans.Web/Controllers/CalendarController.cs
-->
<!-- freshness:flag-on-change
  Calendar event/recurrence rules, soft-delete, audit-log triggers, and open-edit authorization model — review when Calendar service/entities/controller change.
-->

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
| OwningTeamId | Guid | FK → Team (`OnDelete: Restrict`). The `OwningTeam` nav property exists on the entity but is `[Obsolete]`-marked per design-rules §6c — EF references it in `CalendarEventConfiguration` under `#pragma warning disable CS0618` purely to wire the FK + cascade behavior. The Application service stitches team display names via `ITeamService.GetTeamNamesByIdsAsync` (§6b). |
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
| Any authenticated human | View all calendar events (month grid, list, agenda views, per-team month view, filter by team). Create, edit, delete events on any team. Cancel or override single occurrences of recurring events. All changes recorded in the audit log |
| Admin | Same as any authenticated human. No additional calendar-specific privileges in v1 |

The calendar is intentionally open: no resource-based authorization gates edit/delete/cancel. View-side edit/delete buttons render from `CalendarEventViewModel.CanEdit` (currently hard-coded to `true` in `CalendarController.Event` to express the open-edit policy in one place — flip the flag here when a tier check is added). Accountability is via the audit log (`IAuditLogService`), which records who performed each mutation.

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

- Every mutation writes an `AuditLogEntry` via `IAuditLogService` (`CalendarEventCreated`, `CalendarEventUpdated`, `CalendarEventDeleted`, `CalendarOccurrenceCancelled`, `CalendarOccurrenceOverridden`). Event-level mutations (create/update/delete) pass `relatedEntityId: ev.OwningTeamId` / `relatedEntityType: nameof(Team)` for team-scoped audit filtering; per-occurrence mutations (cancel/override) do not.
- Every mutation invalidates the in-service short-TTL `IMemoryCache` entry `calendar:active-events` (§15f request-acceleration cache, not a canonical projection).

## Cross-Section Dependencies

- **Teams:** `ITeamService.GetTeamNamesByIdsAsync` — owning-team display names are stitched in-memory (§6b) instead of `.Include(e => e.OwningTeam)`. `ITeamService.GetAllTeamsAsync` / `GetTeamByIdAsync` populate the team picker on Create/Edit/Index/Team views. Event-level audit entries reference the owning team as `relatedEntityId` for team-scoped audit filtering.
- **Users/Identity:** `CreatedByUserId` is persisted on the entity; every subsequent mutation logs the actor via the audit log (no `UpdatedByUserId` column).
- **Audit Log:** `IAuditLogService` — every mutation writes an entry. The `Event` view embeds the `AuditLog` view component scoped to `entityType = nameof(CalendarEvent)`, `entityId = event.Id`.

## Architecture

**Owning services:** `CalendarService`
**Owned tables:** `calendar_events`, `calendar_event_exceptions`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#569, 2026-04-23, design-rules §15i).

- Service lives in `Humans.Application/Services/Calendar/CalendarService.cs` and never imports `Microsoft.EntityFrameworkCore` (enforced by the project's reference graph, design-rules §2b).
- `ICalendarRepository` (impl in `Humans.Infrastructure/Repositories/Calendar/CalendarRepository.cs`) is the only code path that touches `calendar_events` / `calendar_event_exceptions` via `DbContext`.
- **Decorator decision** — no caching decorator. Rationale: low-traffic community calendar; the short-TTL `IMemoryCache` entry `calendar:active-events` stays in-service per §15f as a request-acceleration marker, not a canonical projection.
- **Cross-domain navs** — `CalendarEvent.OwningTeam` is `[Obsolete]`-marked per §6c; EF references it under `#pragma warning disable CS0618` in `CalendarEventConfiguration` solely to declare the FK + cascade. Display stitching routes through `ITeamService.GetTeamNamesByIdsAsync` (§6b in-memory join). Aggregate-local nav `CalendarEvent.Exceptions` is kept and eagerly loaded by the repository.
- **Cross-section calls** — public interfaces this section consumes: `ITeamService` (display names, team picker), `IAuditLogService` (mutation audit).
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/CalendarArchitectureTests.cs` pins the §15 shape.

### Touch-and-clean guidance

- When adding new controller actions, route through `ICalendarService` — do not inject `HumansDbContext` into `CalendarController`.
- Do not add `.Include(e => e.OwningTeam)` or `.Include(e => e.CreatedByUser)` — the entity carries FKs only.
- Every new mutation must write an `AuditLogEntry` via `IAuditLogService`; do not skip audit for "admin convenience" operations.
- Every new page must have a nav link (CLAUDE.md coding rules — no orphan pages).
