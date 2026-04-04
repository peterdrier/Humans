# Tickets — Section Invariants

## Concepts

- **Ticket Orders** and **Ticket Attendees** are records synced from an external ticket vendor. They are not manually created in the system.
- A **Ticket Order** represents a purchase (one per transaction). It is enriched with Stripe fee data (payment method, Stripe fee, application fee) during sync.
- A **Ticket Attendee** represents an individual ticket holder (one per issued ticket, multiple per order).
- **Auto-matching** links orders and attendees to humans in the system by email address.
- **Ticket Sync** is a background job that pulls order and attendee data from the vendor.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| TicketAdmin, Board, Admin | View the ticket dashboard (sales, revenue, fee breakdowns). View orders and attendees |
| TicketAdmin, Admin | Trigger ticket sync. Generate discount codes. Export ticket data |
| Admin | Manage ticket sync configuration. Execute manual vendor API operations |

## Invariants

- Ticket orders and attendees are synced from the external vendor — they cannot be manually created or edited.
- Orders are enriched with Stripe fee data during sync.
- Orders and attendees are auto-matched to humans by email address during sync.
- Ticket sync state is a singleton tracking the last sync time and status.

## Negative Access Rules

- Board **cannot** trigger ticket sync, generate codes, or export data. Board can only view the dashboard, orders, and attendees.
- TicketAdmin **cannot** manage sync configuration or execute manual vendor API operations.
- Regular humans have no access to ticket management or the ticket dashboard.

## Triggers

- When ticket sync runs, new orders and attendees are imported and existing ones are updated.
- Auto-matching runs during sync: orders and attendees are matched to humans by email.

## Cross-Section Dependencies

- **Campaigns**: TicketAdmin can generate discount codes for campaigns via the ticket vendor integration.
- **Profiles**: Ticket orders and attendees are auto-matched against human email addresses.
- **Admin**: Sync configuration and manual vendor operations are Admin-only.
