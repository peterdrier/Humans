<!-- freshness:triggers
  src/Sections/Humans.Tickets/**
  src/Sections/Humans.Shifts/Services/BurnSettingsService.cs
  src/Sections/Humans.Gate/Services/GateService.cs
  src/Sections/Humans.Shifts/Domain/EventSettings.cs
  src/Sections/Humans.Shifts/Data/Configurations/EventSettingsConfiguration.cs
-->
<!-- freshness:flag-on-change
  EventParticipation entity, status lifecycle transitions, or "Who Hasn't Bought" exclusion rule may have shifted.
-->

# Event Participation Tracking

## Business Context

Track yearly event participation status for each human, enabling self-service opt-out for those not attending and keeping those humans off the admin "Who Hasn't Bought" list.

## User Stories

### As a human, I want to declare I'm not attending this year
- Dashboard ticket widget shows "Not attending this year" button when no ticket linked (the widget is hidden while the active event is off season)
- Clicking sets participation status to NotAttending
- Can undo the declaration to return to default state

### As a ticket holder, my participation is auto-tracked
- Ticket sync creates/updates participation records automatically
- Valid ticket -> Ticketed status
- Checked in -> Attended status (permanent, cannot revert); written by the ticket sync from per-ticket `CheckedInAt`, by contact import from the earliest scan among newly matched attendees, and directly by a Humans gate admit
- Ticket purchase overrides a previous NotAttending declaration
- Ticket void/transfer with no remaining tickets removes Ticketed record

### As an admin, I can see who still needs a ticket
- "Who Hasn't Bought" excludes humans who declared not attending

### As a human (or a ticket admin), I can see when I arrived onsite
- My own profile shows an "Onsite since {time}" chip once I'm Attended with a gate check-in for the active event year; ticket admins, Board and Admin see it on anyone's profile; nobody else does

### As an admin, I can backfill historical data
- CSV import of UserId,Status pairs for a given year
- Available via Tickets > Backfill tab (admin only)

## Data Model

### EventParticipation (`tickets_event_participations`, Tickets-owned since Users decomposition commit 13)
| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | User id (no FK — cross-section) |
| Year | int | Event year |
| Status | ParticipationStatus? | NotAttending, Ticketed, Attended, NoShow; `null` = cleared (kept only where a legacy Users row backs that user and year) |
| DeclaredAt | Instant? | When user self-declared NotAttending |
| CheckedInAt | Instant? | Earliest gate check-in for the year; write-once (never overwritten once set) |
| Source | ParticipationSource | UserDeclared, TicketSync, AdminBackfill |

**Unique constraint:** (UserId, Year)

### EventSettings.Year
Added `Year` (int) to EventSettings so participation records can be linked to the correct event year.

## Status Lifecycle

| Transition | Trigger |
|---|---|
| (none) -> NotAttending | User clicks "Not attending this year" |
| (none) -> Ticketed | Ticket sync matches valid ticket |
| NotAttending -> Ticketed | Ticket purchase overrides |
| Ticketed -> Attended | Ticket sync sees a gate scan (`TicketAttendee.CheckedInAt`, synced from the vendor's `/check_ins` — a scanned ticket's Status stays Valid) |
| any (except Attended) -> Attended | Gate admit: `GateService` projects a recorded admit onto participation (CheckedInAt = admit instant), best-effort after the durable scan event — so consumed camp Early Entry can't be revoked |
| Ticketed -> NoShow | Reserved — the enum value exists, nothing writes it yet |
| Ticketed -> (removed) | All valid tickets voided/transferred |

Attended is permanent -- cannot be reverted by any mechanism.

## Legacy data and the move from Users

Until Users decomposition commit 13 the records lived in Users' `event_participations`. That table is now frozen: nothing writes it, and the only reader is `EventParticipationService`, which answers a user and year with no Tickets record from the legacy row and seeds every write from it. A removal that a legacy row backs leaves a cleared record so the legacy value cannot return. The `tickets.event-participations` data migration (`/Debug/DataMigrations`) copies the legacy rows; a later PR retires the fallback and drops the table.

## Account merge

`EventParticipationService` (`IUserMerge`) folds the merged-away account's years into the survivor: the higher status wins (`Attended > Ticketed > NoShow > NotAttending`) and brings its source and declaration time; the first check-in time is kept. The source's records are cleared.

## GDPR

Export slice `EventParticipations` (Tickets). Retained on erasure, unchanged from when Users owned the table.

## Related Features

- Ticket sync (TicketSyncService)
- Dashboard ticket widget
- Ticket admin dashboard
- "Who Hasn't Bought" metrics
