<!-- freshness:triggers
  src/Humans.Application/Services/Tickets/TicketTransferService.cs
  src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs
  src/Humans.Application/Interfaces/Repositories/ITicketTransferRepository.cs
  src/Humans.Domain/Entities/TicketTransferRequest.cs
  src/Humans.Domain/Enums/TicketTransferStatus.cs
  src/Humans.Domain/Enums/TicketTransferVendorResult.cs
  src/Humans.Infrastructure/Repositories/Tickets/TicketTransferRepository.cs
  src/Humans.Infrastructure/Services/TicketTailorService.cs
  src/Humans.Infrastructure/Services/StubTicketVendorService.cs
  src/Humans.Web/Controllers/TicketTransferController.cs
  src/Humans.Web/Views/TicketTransfer/
-->
<!-- freshness:flag-on-change
  Vendor writeback state machine (Succeeded / VoidSucceededIssueFailed / Failed), recipient lookup contract, audit-log emission points, dashboard nav badge wiring — review when transfer lifecycle, vendor integration, or admin queue surface changes.
-->

# Ticket Transfer

## Business Context

Tickets to the annual gathering sell out, and reality changes between purchase and the event: a buyer's life intervenes (going abroad, an emergency, a long-planned vacation collides with the dates), and they want a known person to take their place rather than waste the seat. Without a sanctioned transfer path, the only options are (a) eat the cost, (b) DIY a vendor-side reissue manually with admin help every time, or (c) hand the ticket QR code to a friend and hope nobody notices the name mismatch. Option (c) is the most common in practice and is exactly what we want to disincentivize — it bypasses verified-member checks, the audit trail, and the recipient's consent to legal docs.

This feature lets a buyer initiate a transfer to another verified Humans member, with a Ticket Admin reviewing each request before the vendor (TicketTailor) is called to void the original and reissue under the recipient's name.

Tracked in: peterdrier/Humans#382.

## User Stories

### US-42.1: Buyer requests a transfer
**As a** buyer with a confirmed ticket
**I want to** request that the ticket be transferred to a specific other Humans member
**So that** the seat goes to someone I trust rather than being wasted

**Acceptance Criteria:**
- Dashboard shows each of the buyer's attendee rows with a "Transfer" button when no transfer is in flight (single-ticket and multi-ticket holders both see the button)
- Recipient lookup accepts a full email address (case-insensitive, exact match) or a burner name (case-insensitive substring). Email queries return at most one candidate (no fuzzy leak); name queries return up to 10 candidates so the buyer picks the right one from a list when more than one matches
- A baseball-card preview of the recipient (display name, picture, burner name, optional preferred email) renders inline before submission so the buyer confirms identity before committing
- Submit attaches a free-text reason (≤1000 chars) visible to admin
- Successful submit shows the row with a "Pending review" badge and a Cancel button on the dashboard

### US-42.2: Buyer cancels a pending transfer
**As a** buyer who realizes they typed the wrong recipient or changed their mind
**I want to** cancel a pending transfer without admin intervention
**So that** I can re-request without a back-channel ping to admin

**Acceptance Criteria:**
- Cancel button appears on the dashboard's pending-transfer row
- Cancel transitions the request to `Cancelled` (audit-logged), removes the badge, and re-enables the Transfer button on the original attendee row
- Cancel is only permitted for `Pending` requests owned by the current user

### US-42.3: Ticket Admin reviews and decides
**As a** Ticket Admin
**I want to** see a queue of pending transfer requests with the requester's reason and recipient identity
**So that** I can approve legitimate transfers and reject suspicious ones

**Acceptance Criteria:**
- `Tickets/Admin/Transfers` index lists all `Pending` rows with requester / recipient display + reason
- Detail page surfaces the same baseball-card recipient view + an Admin Notes textarea
- Approve and Reject actions are policy-gated to `TicketAdminOrAdmin`
- Decision (with notes) is captured on the `TicketTransferRequest` row and audit-logged
- Admin nav tree shows a count badge of pending requests for at-a-glance triage

### US-42.4: Vendor writeback (TicketTailor void+reissue, Option B)
**As a** Ticket Admin approving a transfer
**I want** the system to void the original ticket and reissue under the recipient's name at the vendor
**So that** the at-event scanner sees the recipient's QR, not the original buyer's

**Acceptance Criteria:**
- On approval the service calls `TicketTailorService.VoidIssuedTicketAsync(voidToHold: true)` then `IssueTicketAsync` against the returned hold
- All four `TicketTransferVendorResult` outcomes are recorded:
  - `NotAttempted` — the request is still `Pending` or was `Rejected` / `Cancelled`
  - `Succeeded` — both legs OK; original `TicketAttendee` flips to `Void` locally, new attendee row materializes, recipient sees the ticket on their dashboard
  - `VoidSucceededIssueFailed` — original is voided at the vendor and locally; admin can retry just the issue half. Hold ID is preserved in `VendorMessage`
  - `Failed` — neither leg succeeded; no local attendee changes; transfer is recorded as `Approved` with vendor failure visible to admin (Option-C fallback)
- Vendor errors are wrapped in `TicketVendorWriteException` with a `Kind` enum (`Validation` / `AuthFailed` / `NotFound` / `RateLimited` / `Transient`) for structured logging

### US-42.5: Option-C admin fallback
**As a** Ticket Admin facing a vendor outage during transfer approval
**I want to** complete the transfer in the Humans record and finish the vendor side manually later
**So that** the recipient still sees their ticket on the dashboard and the team has an audit trail of what was decided

**Acceptance Criteria:**
- On `VendorResult = Failed` or `VoidSucceededIssueFailed`, the request still ends in `Approved` state for admin follow-up
- `VendorMessage` records the raw error so admin can decide whether to retry the vendor call later or edit the TT dashboard manually
- Audit-log message distinguishes the three vendor outcomes so log readers can spot Option-C cases at a glance

### US-42.6: Sync resilience for null vendor order id
**As a** Ticket Admin watching the periodic Ticket Tailor sync
**I want** the sync to handle TT responses that omit `order_id` without crashing or duplicating attendees
**So that** legitimate vendor weirdness (orphan tickets, hold-only issues) doesn't poison the local mirror

**Acceptance Criteria:**
- TT issued tickets with null `order_id` are looked up by `VendorTicketId` against existing local rows; if found, the existing `TicketOrderId` is reused
- If no local row exists, the attendee is logged and skipped rather than inserted with a missing parent FK
- New attendees pre-populated by `TicketTransferService.WriteToVendorAsync` are not double-counted on the next sync run

## State Machine

```
Submitted (status=Pending, vendor=NotAttempted)
   │
   ├── Cancel  → status=Cancelled, vendor=NotAttempted   (terminal)
   ├── Reject  → status=Rejected, vendor=NotAttempted    (terminal)
   └── Approve → status=Approved
                   │
                   ├── vendor=Succeeded                       (happy path)
                   ├── vendor=VoidSucceededIssueFailed       (admin retry the issue half)
                   └── vendor=Failed                          (Option-C fallback)
```

Triggers: `Submit` (buyer), `Cancel` (buyer, only on Pending owned), `Reject` (admin), `Approve` (admin).

## Recipient Lookup Contract

`ITicketTransferService.LookupRecipientsAsync` is **case-insensitive** and **exact-match-on-email** (no fuzzy / partial / wildcard semantics) to avoid accidental cross-matches between similar email addresses. Email queries return zero or one candidate (the verified-email row, if any). Burner-name queries use case-insensitive `Contains` matching against the consolidated `PersonSearchFields.Name` bucket (covers `BurnerName` + the underlying `User.DisplayName`) and return up to 10 candidates ordered by display name — the buyer flow surfaces the list and lets the user pick. The picker resolves a chosen `UserId` back to a single baseball card via `GetRecipientCardAsync`. Suspended or unapproved profiles are filtered out at the search layer (security-adjacent — no transferring to suspended users).

The four recipient fields surfaced in the baseball card (display name, picture, burner name, optional preferred email) are fixed by spec; the buyer flow does not re-invoke `ProfileCardViewComponent` because that would leak unrelated profile fields (teams, CV, bio) into the transfer surface.

## Audit Log

Every state transition writes an `AuditLog` row via `IAuditLogService.LogAsync`:

| Action | Trigger | Description shape |
|--------|---------|---------------------|
| `TicketTransferRequested` | Buyer submits | `"Transfer requested: <recipient display>"` |
| `TicketTransferCancelled` | Buyer cancels | `"Transfer cancelled by requester"` |
| `TicketTransferRejected` | Admin rejects | `"Transfer rejected" [: <admin notes>]` |
| `TicketTransferApproved` | Admin approves | Vendor-result-aware (`Succeeded` / `VoidSucceededIssueFailed` / `Failed`) — see `TicketTransferService.ApproveAsync` |

A partial-state path also exists: if the vendor write succeeds but the local `UpdateAsync` of the request row fails, an explicit `PARTIAL STATE` audit row is emitted before the exception is rethrown so admin can reconcile manually.

## Workflows

```
Buyer dashboard (Home/Index → Dashboard.cshtml)
  ├─ Transfer button → TicketTransferController.Request (GET)
  │     → Request.cshtml (lookup form)
  │        → Lookup (POST) → 0 results: same form with "no match" hint
  │                          1 result : Request.cshtml with baseball-card
  │                          >1 result: Request.cshtml with picker list,
  │                                     PickRecipient (POST) → baseball-card
  │           → Submit (POST)
  │              → TicketTransferService.CreateRequestAsync
  │              → audit log, redirect to Home/Index with SetSuccess
  └─ Cancel button on Pending row → TicketTransferController.Cancel (POST)
        → TicketTransferService.CancelAsync
        → audit log, redirect to Home/Index with SetSuccess

Admin queue (TicketAdminOrAdmin)
  ├─ AdminNavTree "Transfer requests" badge (count from CountPendingAsync)
  │     → TicketTransferAdminController.Index
  │        → TicketTransferService.GetByStatusAsync(Pending) → Index.cshtml
  └─ Detail link
        → TicketTransferAdminController.Detail
           → Detail.cshtml (admin notes + Approve/Reject)
              → Decide (POST)
                 → ApproveAsync / RejectAsync
                    → ApproveAsync: WriteToVendorAsync (TT void+reissue) → UpdateAsync → audit log
                    → RejectAsync: UpdateAsync → audit log
                 → redirect to Index with SetSuccess/SetError
```

## Related Features

- [`19-tickets.md`](19-tickets.md) — base ticket model, sync architecture, attendee linkage to users
- [`32-budget-and-projections.md`](32-budget-and-projections.md) — `TicketingBudgetService` consumes the same `ticket_attendees` table for projections
- [`docs/sections/Tickets.md`](../sections/Tickets.md) — section invariants, cross-section dependencies, table ownership

## Open Follow-ups

- **Pricing parity on reissue.** `TicketTransferService.WriteToVendorAsync` snapshots the original attendee's `Price` onto the new local row, but TicketTailor may rebill the recipient at a current ticket-type price. Reconciling the two is on the backlog (probe `Open Questions` in the design doc).
- **Surfacing decided transfers.** The admin queue only shows `Pending` rows. A history view of decided transfers (filtered by date / status / vendor result) would help with the Option-C reconciliation workflow.
- **Recipient consent before reissue.** Currently the recipient sees the ticket appear post-approval without an explicit accept step. A future iteration could prompt the recipient to acknowledge before the vendor reissue runs, gated on a feature flag.
