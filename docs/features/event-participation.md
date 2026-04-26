<!-- freshness:triggers
  src/Humans.Application/Services/Tickets/**
  src/Humans.Application/Services/Shifts/ShiftManagementService.cs
  src/Humans.Web/Controllers/TicketController.cs
  src/Humans.Domain/Entities/EventParticipation.cs
  src/Humans.Domain/Entities/EventSettings.cs
  src/Humans.Infrastructure/Data/Configurations/Shifts/EventParticipationConfiguration.cs
  src/Humans.Infrastructure/Data/Configurations/Shifts/EventSettingsConfiguration.cs
-->
<!-- freshness:flag-on-change
  EventParticipation entity, status lifecycle transitions, or "Who Hasn't Bought" exclusion rule may have shifted.
-->

# Event Participation Tracking

## Business Context

Track yearly event participation status for each human, enabling self-service opt-out for those not attending and providing admin visibility into participation breakdown.

## User Stories

### As a human, I want to declare I'm not attending this year
- Dashboard ticket widget shows "Not attending this year" button when no ticket linked
- Clicking sets participation status to NotAttending
- Can undo the declaration to return to default state

### As a ticket holder, my participation is auto-tracked
- Ticket sync creates/updates participation records automatically
- Valid ticket -> Ticketed status
- Checked in -> Attended status (permanent, cannot revert)
- Ticket purchase overrides a previous NotAttending declaration
- Ticket void/transfer with no remaining tickets removes Ticketed record

### As an admin, I can see participation breakdown
- Donut chart on ticket dashboard: Has Ticket / No Ticket / Not Coming
- "Who Hasn't Bought" excludes humans who declared not attending

### As an admin, I can backfill historical data
- CSV import of UserId,Status pairs for a given year
- Available via Tickets > Backfill tab (admin only)

## Data Model

### EventParticipation
| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK to User (Cascade) |
| Year | int | Event year |
| Status | ParticipationStatus | NotAttending, Ticketed, Attended, NoShow |
| DeclaredAt | Instant? | When user self-declared NotAttending |
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
| Ticketed -> Attended | Ticket sync sees CheckedIn |
| Ticketed -> NoShow | Post-event derivation |
| Ticketed -> (removed) | All valid tickets voided/transferred |

Attended is permanent -- cannot be reverted by any mechanism.

## Related Features

- Ticket sync (TicketSyncService)
- Dashboard ticket widget
- Ticket admin dashboard
- "Who Hasn't Bought" metrics
