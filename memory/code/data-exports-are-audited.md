---
name: data-exports-are-audited
description: HARD RULE. Every bulk export/download of personal data (CSV, file) writes an audit entry (actor, what, row count) and carries the narrowest role — never a silent GET; adding one is a decision for Peter first.
type: code
---

Any endpoint that hands a person a file of other people's data — names, emails, purchases — writes an `IAuditLogService.LogAsync` entry with the actor, what was exported and the row count, and carries the tightest policy that serves the need (`AdminOnly` unless a role genuinely needs it). Columns are the minimum the recipient asked for.

**Why:** Peter, 2026-09-30, on a donor-list CSV added without a word: "we rarely do that now, and you just added a new way without question … data exports get audited." A download is a disclosure path leaving the system; the Board must be able to see it happened (AGENTS.md, "The Board can see what happened"), and the existing unaudited exports are debt, not precedent.

**How to apply:** Put the audit call in the service that builds the rows (controllers hold no logic), next to the filter, so every caller pays it. Before adding any new export, say so and get Peter's call on columns, role, and whether an endpoint should exist at all versus a one-off pull. Live example: `TicketQueryService.GetDonationExportDataAsync`. Known debt: `/Tickets/Export/Orders` and `/Tickets/Export/Attendees` (Tickets `Docs/debt.yml` TICKETS-9). Related: [`audit-pii-subject-allowed`](audit-pii-subject-allowed.md).
