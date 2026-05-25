<!-- freshness:triggers
  src/Humans.Application/Services/Tickets/TicketTransferService.cs
  src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs
  src/Humans.Application/Interfaces/Repositories/ITicketTransferRepository.cs
  src/Humans.Domain/Entities/TicketTransferRequest.cs
  src/Humans.Domain/Enums/TicketTransferStatus.cs
  src/Humans.Infrastructure/Repositories/Tickets/TicketTransferRepository.cs
  src/Humans.Web/Controllers/TicketTransferController.cs
  src/Humans.Web/Controllers/TicketTransferAdminController.cs
  src/Humans.Web/Views/TicketTransfer/
  src/Humans.Web/Views/TicketTransferAdmin/
  src/Humans.Web/ViewComponents/TicketStubViewComponent.cs
-->
<!-- freshness:flag-on-change
  Manual-processing model (no vendor writeback), lifecycle states, email notification points,
  the AllowEmail recipient lookup, and the reusable ticket-stub — review when transfer lifecycle,
  the wizard, or notifications change.
-->

# Ticket Transfer

## Business Context

Tickets to the annual gathering sell out, and reality changes between purchase and the event: a buyer's
life intervenes and they want a known person to take their place rather than waste the seat. Without a
sanctioned path the common workaround is handing over the QR code and hoping nobody notices the name
mismatch — which bypasses verified-member checks, the audit trail, and the receiver's consent to legal
docs.

This feature lets the original holder (the **Sender**) request a transfer to another verified Humans
member (the **Receiver**). **For this year the ticket team performs the actual void+reissue manually in
the TicketTailor dashboard** — Humans does not call the vendor. A request notifies the team; once they
process it they mark it successful (or cancel it with a reason), and the next ticket sync reconciles the
local attendee rows automatically.

Tracked in: peterdrier/Humans#382. The earlier automated void+reissue engine (Option B / Option C /
retry-issue / vendor-step timeline) was removed when transfers moved to manual processing.

## User Stories

### US-42.1: Sender requests a transfer (one-page wizard)
**As a** Sender (current holder of a `Valid` ticket)
**I want to** request sending the ticket to a specific other Humans member
**So that** the seat goes to someone I trust rather than being wasted

**Acceptance Criteria:**
- The wizard lives at `/Tickets/Transfers` (no `/Send`, no `attendeeId` query param). The homepage and
  `/Profile/Me` link to it; the homepage shows the held tickets as physical admission stubs.
- **Step A:** the Sender's transferable tickets render as admission stubs (`<vc:ticket-stub>`); pick one.
- **Step B:** the recipient is chosen with the standard `<vc:human-search>` component (`scope=Name`,
  `allow-email=true`) — search by burner name, or paste an exact email to resolve a single match.
- **Step C:** a server-side confirm step resolves the Receiver's legal name + primary email (the search
  API omits legal name) and shows: *"This will request transferring ticket X to <legal name> — <email>.
  Our ticketing team will process this and let you know shortly."* plus an optional reason (≤1000 chars).
- On submit a `Pending` request is created; the Sender sees a pending stamp on the ticket and a Cancel
  control on the homepage.

### US-42.2: Sender cancels a pending transfer
- A Cancel control appears on the Sender's pending-transfer ticket on the homepage.
- Cancel transitions the request to `Cancelled` (audit-logged) and re-enables transferring the ticket.
- Cancel is only permitted for `Pending` requests where the caller is the Sender.

### US-42.3: Request notifications
- On request, an email goes to the **Sender** (confirmation) and to **tickets@nobodies.team** (action
  needed, linking to the admin detail page).

### US-42.4: Ticket team processes and decides
**As a** Ticket Admin
**I want to** process the transfer manually in TicketTailor and then record the outcome
**So that** the request queue reflects reality and the parties are notified

**Acceptance Criteria:**
- `/Tickets/Admin/Transfers` lists `Pending` rows (FIFO) plus an "All" tab; an order-drift table flags
  paid orders whose valid-ticket count dropped below what was issued (manual-reconciliation aid).
- The Detail page shows the ticket/order context, both parties, the reason, a "View order in
  TicketTailor" link, and manual-processing instructions.
- **Mark transfer successful** sets the request `Approved`; **Cancel transfer** requires a reason and
  sets it `Rejected`. Both are policy-gated to `TicketAdminOrAdmin` and audit-logged.
- Neither action calls the vendor or mutates attendee rows — the next ticket sync picks up the team's
  TicketTailor-side void/reissue.

### US-42.5: Decision notifications
- On a decision, an email goes to **both the Sender and the Receiver**: completed, or cancelled with the
  reason.

## State Machine

```
Submitted (Pending)
   ├── Cancel (Sender)              → Cancelled    (terminal)
   ├── Cancel transfer (admin)      → Rejected     (reason required, terminal)
   └── Mark successful (admin)      → Approved      (terminal; no vendor call)
```

Triggers: `Submit` (Sender), `Cancel` (Sender, only on own Pending), `Reject`/`Approve` (admin).

## Recipient Lookup

Recipients are found with the canonical `<vc:human-search>` inline picker (no bespoke lookup). Burner
name is a case-insensitive search; with `allow-email=true` an `@`-containing query is an exact,
case-insensitive verified-email match returning at most one person (no enumeration leak). See
[`memory/architecture/person-search.md`](../../../memory/architecture/person-search.md).

## Audit Log

| Action | Trigger | Description shape |
|--------|---------|---------------------|
| `TicketTransferRequested` | Sender submits | `"Transfer requested: ticket <vendorTicketId> → <Receiver legal name>"` |
| `TicketTransferCancelled` | Sender cancels | `"Transfer cancelled by Sender"` |
| `TicketTransferApproved` | Admin marks successful | `"Transfer marked successful (processed manually in TicketTailor)"` |
| `TicketTransferRejected` | Admin cancels | `"Transfer cancelled: <reason>"` |

## Reusable Ticket Stub

`<vc:ticket-stub>` renders one held ticket as a physical admission stub (event label, attendee name +
email, serial). A pending outgoing transfer shows a "transfer pending" stamp; voided tickets render
muted. Used by the wizard (step A), the `/Profile/Me` ticket card (`<vc:ticket-holdings>`), and the
homepage "You're in" ticket card.

## Dormant Storage

The removed vendor engine's columns (`VendorResult`, `VendorMessage`, `NewVendorTicketId`,
`VendorStepsJson`) remain on `ticket_transfer_requests` as **dormant, unread** columns. Per
[`memory/architecture/no-drops-until-prod-verified.md`](../../../memory/architecture/no-drops-until-prod-verified.md)
a follow-up PR drops them after prod soak.

## Related

- [`docs/sections/Tickets.md`](../../sections/Tickets.md) — section invariants, sync, attendee model.
- [`docs/features/budget/budget.md`](../budget/budget.md) — `TicketingBudgetService` shares the attendee table.
