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
  The two processing paths (automated void+reissue vs manual mark-successful), lifecycle states, email
  notification points, the AllowEmail recipient lookup, and the reusable ticket-stub — review when
  transfer lifecycle, the wizard, the vendor writeback, or notifications change.
-->

# Ticket Transfer

## Business Context

Tickets to the annual gathering sell out, and reality changes between purchase and the event: a buyer's
life intervenes and they want a known person to take their place rather than waste the seat. Without a
sanctioned path the common workaround is handing over the QR code and hoping nobody notices the name
mismatch — which bypasses verified-member checks, the audit trail, and the receiver's consent to legal
docs.

This feature lets the original holder (the **Sender**) request a transfer to another verified Humans
member (the **Receiver**). A request notifies the team, who process it one of two ways:

- **Automated** (`ProcessTransferAsync` — the **Process transfer** button): Humans voids the original ticket
  *to a hold* and reissues the same ticket type from that hold to the Receiver via the TicketTailor API.
  The void never refunds or moves the order's money; the reissue is like-for-like (same class even if it's
  now closed/sold out) and carries the original price. The swapped attendee rows are written locally in the
  same flow.
- **Manual** (`ApproveAsync` = "mark successful"): the team voids+reissues by hand in the TicketTailor
  dashboard, then records the outcome; the next ticket sync reconciles the local attendee rows.

Either way the team can instead cancel the request with a reason. Both decision actions are admin-only
(`TicketAdminOrAdmin`); there is no per-environment toggle — the automated path is always available to the
ticket team.

Tracked in: peterdrier/Humans#382. The void+reissue mechanics are researched in
[`docs/superpowers/probes/2026-05-04-tickettailor-write-api.md`](../../superpowers/probes/2026-05-04-tickettailor-write-api.md)
and [`…-tt-reissue-verification.md`](../../superpowers/probes/2026-05-04-tt-reissue-verification.md). The
earlier engine's retry-issue + vendor-step timeline UI were **not** restored — outcomes are recorded on the
request columns + audit log instead.

## User Stories

### US-42.1: Sender requests a transfer (one-page wizard)
**As a** Sender (current holder of a `Valid`, not-yet-checked-in ticket)
**I want to** request sending the ticket to a specific other Humans member
**So that** the seat goes to someone I trust rather than being wasted

**Acceptance Criteria:**
- The wizard lives at `/Tickets/Transfers` (no `/Send`, no `attendeeId` query param). The homepage and
  `/Profile/Me` link to it; the homepage shows the held tickets as physical admission stubs.
- **Step A:** the Sender's transferable tickets render as admission stubs (`<vc:ticket-stub>`); pick one.
- Gate-checked-in tickets are not transferable: a gate scan keeps the ticket's `Status = Valid` but stamps
  `TicketAttendee.CheckedInAt`, so transferability requires `Status == Valid` **and** `CheckedInAt == null`
  (guarded in the stub list's `CanSendTransfer`, the confirm step, and `CreateRequestAsync`).
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
**I want to** process the transfer (automatically, or by hand in TicketTailor) and record the outcome
**So that** the request queue reflects reality and the parties are notified

**Acceptance Criteria:**
- `/Tickets/Admin/Transfers` lists `Pending` rows (FIFO) plus an "All" tab; an order-drift table flags
  paid orders whose valid-ticket count dropped below what was issued (manual-reconciliation aid).
- The Detail page shows the ticket/order context, both parties, the reason, a "View order in
  TicketTailor" link, and processing instructions that match the active path.
- **Process transfer** voids the original ticket
  to a hold and reissues the same ticket type to the Receiver via the TicketTailor API, writes the swapped
  attendee rows, and sets `Approved` (`VendorResult = Succeeded`). On vendor failure the request **stays
  `Pending`** with the diagnostic recorded (`VendorResult` `Failed`, or `VoidSucceededIssueFailed` with the
  hold id persisted to `VendorHoldId`) and no success emails fire. A `VoidSucceededIssueFailed` request
  (ticket already voided) shows a **Retry reissue** button that re-issues straight from the held seat — one
  click, no dashboard step — or the admin can finish in TicketTailor and **Mark successful**. Such a request
  **cannot be cancelled, rejected, or re-processed** (that would strand or double-void the held seat).
- **Mark transfer successful** sets `Approved` with no vendor call (manual void+reissue, or closing out a
  partial automated attempt); **Cancel transfer** requires a reason and sets `Rejected`. All three are
  policy-gated to `TicketAdminOrAdmin` and audit-logged.
- The manual path mutates no attendee rows — the next ticket sync picks up the team's TicketTailor-side
  void/reissue (which re-links the API-issued reissue to the original order and preserves its price).

### US-42.5: Decision notifications
- On a decision, an email goes to **both the Sender and the Receiver**: completed, or cancelled with the
  reason.

## State Machine

```
Submitted (Pending)
   ├── Cancel (Sender)              → Cancelled    (terminal)
   ├── Cancel transfer (admin)      → Rejected     (reason required, terminal)
   ├── Process transfer (admin)     → Approved      (terminal; automated TT void+reissue OK)
   │                                  └ vendor failure → stays Pending (diagnostic recorded; hold kept)
   ├── Retry reissue (admin)        → Approved      (from VoidSucceededIssueFailed; re-issues from the held seat)
   │                                  └ still failing → stays Pending (hold retained for another retry)
   └── Mark successful (admin)      → Approved      (terminal; no vendor call)
```

A `VoidSucceededIssueFailed` request (ticket voided, reissue pending) accepts only **Retry reissue** or
**Mark successful** — Cancel/Reject/Process are blocked so the already-voided seat can't be stranded or
double-voided.

Triggers: `Submit` (Sender), `Cancel` (Sender, only on own Pending), `Reject`/`Approve` (manual mark
successful) / `Process` (automated void+reissue) / `Retry` (reissue from the held seat) (admin).

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
| `TicketTransferApproved` | Admin marks successful (manual) | `"Transfer marked successful (processed manually in TicketTailor)"` |
| `TicketTransferApproved` | Admin processes (automated OK) | `"Transfer processed automatically (TT void+reissue OK, new ticket <id>)"` |
| `TicketTransferApproved` | Admin retries reissue (OK) | `"Reissue retried OK (new ticket <id>)"` |
| `TicketTransferAutoFailed` | Automated attempt or retry failed/partial | `"Automated process FAILED/PARTIAL — <detail>…"` / `"Reissue retry failed — <detail>"` (request stays Pending) |
| `TicketTransferRejected` | Admin cancels | `"Transfer cancelled: <reason>"` |

## Reusable Ticket Stub

`<vc:ticket-stub>` renders one held ticket as a physical admission stub (event label, attendee name +
email — the vendor `ti_…` serial was deliberately dropped from the stub). A pending outgoing transfer
shows a "transfer pending" stamp; voided tickets render
muted. Used by the wizard (step A), the `/Profile/Me` ticket card (`<vc:ticket-holdings>`), and the
homepage "You're in" ticket card.

## Vendor-writeback Storage

`VendorResult`, `VendorMessage`, `NewVendorTicketId`, and `VendorHoldId` on `ticket_transfer_requests`
carry the automated void+reissue outcome (written by `ProcessTransferAsync` / `RetryReissueAsync`;
`NotAttempted`/null for manual transfers) and are read by the admin queue/detail. `VendorHoldId` holds the
TicketTailor hold from a void-to-hold whose reissue failed, so **Retry reissue** can re-issue from that
held seat without a dashboard step. `VendorStepsJson` (the removed vendor-step timeline's storage) stays
**dormant, unread**; per
[`memory/architecture/no-drops-until-prod-verified.md`](../../../memory/architecture/no-drops-until-prod-verified.md)
a follow-up PR drops that one column after prod soak.

## Related

- [`docs/sections/Tickets.md`](../../sections/Tickets.md) — section invariants, sync, attendee model.
- [`docs/features/budget/budget.md`](../budget/budget.md) — `TicketingBudgetService` shares the attendee table.
