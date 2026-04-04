# Campaigns — Section Invariants

## Concepts

- A **Campaign** is a bulk code distribution effort — discount codes are assigned to humans and delivered via email waves.
- A **Campaign Code** is an individual code belonging to a campaign. Codes are imported in bulk (CSV) or generated via the ticket vendor.
- A **Campaign Grant** records the assignment of a specific code to a specific human.
- A **Wave** is a batch email send targeting a group of humans (typically by team) who have been granted codes but not yet notified.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| TicketAdmin, Admin | View campaign details, generate discount codes via the ticket vendor |
| Admin | Full campaign management: create, edit, activate, complete campaigns. Import codes. Manage grants. Send campaign email waves |

## Invariants

- Campaign status follows: Draft then Active then Completed.
- Codes can only be generated or imported while the campaign is in Draft status.
- Each code is unique per campaign and can be assigned to at most one human.
- Campaign emails are queued through the email outbox system. Each grant tracks the status and timestamp of the most recent delivery attempt.
- Humans can unsubscribe from campaigns via a link in the email. Unsubscribed humans are excluded from future campaign sends.

## Negative Access Rules

- TicketAdmin **cannot** create, edit, activate, or complete campaigns. They can only view details and generate codes.
- Regular humans and other roles have no access to campaign management.
- There is no self-service view for humans to see their assigned codes (codes are delivered by email).

## Triggers

- When a campaign wave is sent, emails are queued to the outbox for each granted human who has not unsubscribed.
- When a human unsubscribes, their unsubscribe flag is set and they are excluded from all future campaign sends.

## Cross-Section Dependencies

- **Tickets**: TicketAdmin can generate discount codes for campaigns via the ticket vendor integration.
- **Email**: Campaign emails are delivered through the email outbox system.
- **Profiles**: Campaign grants link to a human. The unsubscribe flag lives on the human's account.
