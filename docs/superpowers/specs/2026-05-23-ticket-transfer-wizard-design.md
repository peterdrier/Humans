# Ticket Transfer Wizard + Manual Vendor Handling — Design

**Date:** 2026-05-23
**Branch:** `feat/ticket-transfer-wizard`
**Tracking:** TBD (peterdrier/Humans issue to be filed)
**Supersedes:** the vendor-writeback portions of `docs/features/tickets/ticket-transfer.md` and the
`2026-05-12-ticket-transfer-ui-history-design.md` spec.

## Summary

Two changes, one feature:

1. **The ticket team processes transfers manually in the TicketTailor dashboard this year.** Humans no
   longer calls TicketTailor to void+reissue on approval. This deletes the entire automated
   vendor-writeback engine (void→issue state machine, Option-C fallback, retry-issue, vendor-step
   timeline). A transfer request becomes a *request* that emails the team; the team does the work by
   hand in TT and marks the request successful or cancelled. The next 15-minute ticket sync picks up
   the team's TT-side void/reissue and updates `ticket_attendees` automatically — Humans never mutates
   attendee rows for a transfer.

2. **The Sender flow becomes a one-page wizard at `/Tickets/Transfers`** (dropping the `/Send` segment
   and the `attendeeId` query param). Step A: pick one of your held tickets, drawn as physical
   admission stubs. Step B: choose the recipient with the **standard** `<vc:human-search>` component
   (not the bespoke lookup it uses today). Step C: confirm, with the confirmation sentence and an
   optional reason, then submit.

A small enabling change to the shared search component (`<vc:human-search>` gains an `AllowEmail` flag)
lets the wizard use the canonical component while still supporting exact-email recipient lookup, so no
search code is forked.

## Goals

- Remove all automated TicketTailor writeback from the transfer path.
- Replace the bespoke recipient lookup with the canonical `<vc:human-search>` component.
- One-page Sender wizard: pick ticket → search recipient → confirm.
- Email the Sender + the ticket team on request; email the Sender + Receiver on the team's decision.
- Keep the admin queue + detail page, retooled for the manual workflow.
- Make the admission-stub a reusable `<vc:ticket-stub>` shown in the wizard, on `/Profile/Me`, and on the
  homepage, with pending-transfer state visible on the ticket itself.

## Non-Goals

- No change to ticket sync, matching, VAT, or any other Tickets concern.
- No Receiver-accept step (out of scope, as today).
- No re-enable path / feature flag for automated writeback — the code is deleted, not dormant
  (revivable from git history if next year wants it back).

## The manual-transfer model

Today `TicketTransferService.ApproveAsync` runs `WriteToVendorAsync` (void to hold → issue against hold
→ local attendee writeback) with a three-outcome vendor state machine and an Option-C fallback. All of
that is removed.

New lifecycle (enum `TicketTransferStatus` is unchanged):

```
Submitted (Pending)
   ├── Cancel              → Cancelled   (Sender, only while Pending)        terminal
   ├── Cancel transfer     → Rejected    (admin, REASON REQUIRED)            terminal
   └── Transfer successful  → Approved    (admin, no vendor call)             terminal
```

- **Transfer successful** (admin): sets `Status=Approved`, `DecidedByUserId`, `DecidedAt`,
  `AdminNotes`; audits `TicketTransferApproved`; emails Sender + Receiver. No vendor call, no attendee
  mutation.
- **Cancel transfer** (admin): reason required; sets `Status=Rejected` + decision fields; audits
  `TicketTransferRejected`; emails Sender + Receiver (with reason).
- **Cancel** (sender): unchanged — `Status=Cancelled`, audits `TicketTransferCancelled`.

The UI labels the `Approved` action "Mark transfer successful"; the enum value stays `Approved` to
minimise churn (Peter approved this mapping).

## Sender wizard — `/Tickets/Transfers`

`TicketTransferController`, route base `Tickets/Transfers`:

| Route | Method | Purpose |
|-------|--------|---------|
| `""` | GET | Render the wizard (steps A + B; no `attendeeId` param) |
| `"Confirm"` | POST | (attendeeId, receiverUserId) → re-render wizard with step C populated |
| `""` | POST | (attendeeId, receiverUserId, reason) → create request, send emails, redirect home |
| `"Cancel"` | POST | (id) → Sender self-cancel (unchanged) |

**Step A — pick a ticket.** `GetMyAttendeesAsync` provides the Sender's held, transferable attendees.
Each renders as an admission-stub card (event "Elsewhere 2026", attendee name + email, serial from
`VendorTicketId`) and is a radio that sets `attendeeId`. Tickets with a pending transfer show the
pending badge instead of being selectable.

**Step B — pick a recipient.** `<vc:human-search field-name="receiverUserId" scope="Name"
allow-email="true" exclude-user-ids="@currentUserId">`. The canonical component — no bespoke lookup.

**Step C — confirm.** When a recipient is chosen the page POSTs to `Confirm`. The server validates
ownership/state and resolves the recipient's **legal name + primary email** (the search API
deliberately omits legal name, so this resolution must be server-side), then re-renders the same view
with step C: the confirmation sentence —

> This will request transferring ticket **TKT-…** (*attendee name*) to **<legal name> — <email>**.
> Our ticketing team will process this and let you know shortly.

— an optional reason textarea (≤1000 chars), and a **Request transfer** button. Submitting POSTs to
`""`.

"One page" = one view template with progressive reveal; the `Confirm` POST is only for safe server-side
identity resolution, not a multi-page stepper.

## `<vc:human-search>` — `AllowEmail` flag

A deliberate, authorized expansion of the canonical inline picker so callers that need exact-email
recipient lookup use the standard component instead of forking.

- `HumanSearchViewComponent` + `HumanSearchPickerViewModel` gain `bool AllowEmail` (param
  `allow-email`, default `false`).
- `Default.cshtml` JS appends `&allowEmail=true` to the `/api/profiles/search` fetch when set.
- `ProfileApiController.Search` gains `[FromQuery] bool allowEmail = false`. When `allowEmail` **and**
  the query contains `@`: resolve via `IUserEmailService.GetUserIdByExactEmailAsync` (verified emails,
  exact, case-insensitive, 0-or-1 result) and return that single card (same shape as `GetByUserId`,
  reusing `GetSharedDetailAsync`); rejected/deleted users excluded. Otherwise the existing name/public
  search runs unchanged.

**Privacy:** exact-match-only means no substring/enumeration leak — a caller can only confirm
membership for an address they already know in full. This is the same disclosure the old transfer
lookup already made, now generalized and gated behind the opt-in flag. Safe for non-admin endpoints;
the `Admin` bit (fuzzy email search) remains admin-only and untouched.

`memory/architecture/person-search.md` is updated in the same commit: document the `allow-email`
param in the inline-picker row and add the exact-match-only invariant.

## Reusable ticket stub + pending visibility

The admission-stub graphic from step A becomes a shared ViewComponent so it renders identically
everywhere and pending-transfer state lives on the ticket itself.

`<vc:ticket-stub>` — renders one held ticket as an admission stub: event label, attendee name,
attendee email, serial (`VendorTicketId`). States:

- **Pending transfer** — a diagonal "Transfer pending" stamp + muted body. Driven by the same
  pending-outgoing-transfer data the dashboard already computes.
- **Void** — muted / struck styling (matches today's holdings card).
- **Selectable** — an optional mode (radio) used by wizard step A.

Backing data: a `TicketStubInfo` projection (attendee name, email, `VendorTicketId`, status,
`HasPendingTransfer`, `PendingTransferRequestId`) produced by `ITicketQueryService` — this is the
union of what `GetUserTicketHoldingsAsync` and `GetMyAttendeesAsync` already return, extended with
email + serial + the pending flag. The event label is sourced from the active event
(`IShiftManagementService.GetActiveAsync`); see Open items.

**Placements (all in this PR):**

1. **Wizard step A** — selectable stubs.
2. **`/Profile/Me`** — replace the plain text list inside the existing `<vc:ticket-holdings>` "Tickets"
   card with stubs (card chrome unchanged; multiples stack).
3. **Homepage** (`Home/Dashboard.cshtml`):
   - **Header** — a compact stub beside the "Welcome, {name} 👋" greeting **when the user holds a
     ticket** (falls back to the plain header otherwise). Sits beside the header, does not replace it.
   - **Tickets section** (lower) — the existing held-ticket rows render as stubs (with pending stamp +
     Sender Cancel) above the single "Transfer a ticket?" link.

Pending-transfer visibility (requirement #1) is satisfied by the stamp wherever the stub renders, plus
the existing dashboard badge + Cancel control.

## Data model & migration

`TicketTransferRequest` entity — **remove**: `VendorResult`, `VendorMessage`, `NewVendorTicketId`,
`VendorStepsJson`. **Keep**: `ReceiverLegalName`, `ReceiverEmail` (used by the team + emails),
everything else.

**Delete**: enum `TicketTransferVendorResult`; value object `TicketTransferVendorStep` +
`TicketTransferVendorStepKind`.

**Migration**: a generated EF migration drops the four columns from `ticket_transfer_requests`
(`vendor_result`, `vendor_message`, `new_vendor_ticket_id`, `vendor_steps_json`). Generated via
`dotnet ef`, never hand-edited.

`MyAttendeeRowDto` and `UserTicketHoldingRow` — add `AttendeeEmail` and `VendorTicketId` (for the stub
cards + confirm reference); holdings rows also surface `HasPendingTransfer` / `PendingTransferRequestId`
(or a shared `TicketStubInfo` projection covers both).

## Service changes — `TicketTransferService`

- **Delete**: `LookupReceiversAsync`, `GetReceiverCardAsync`, `BuildReceiverCardAsync`,
  `WriteToVendorAsync`, `RetryIssueAsync`, `AppendStep`, `VendorStepsJsonOptions`. Drop the
  `ITicketVendorService` dependency.
- **Add**: a confirmation-resolve method (e.g. `GetConfirmationAsync(attendeeId, receiverUserId,
  senderUserId)`) returning a DTO with ticket serial + attendee name and recipient legal name + email;
  validates ownership/state so the controller stays thin.
- `CreateRequestAsync`: unchanged validation + snapshot; **adds** sending the request emails
  (Sender + tickets@).
- `ApproveAsync`: strip vendor logic; set decision fields, audit, send completion emails.
- `RejectAsync`: reason required; set decision fields, audit, send cancellation emails.
- Inject `IEmailService`.
- `ITicketTransferService` interface updated to match (remove `RetryIssueAsync`, lookup methods; add
  the confirmation method).

## Emails

`tickets@nobodies.team` is a real Google group; hardcode it as a constant (e.g.
`TicketConstants.TicketsTeamEmail`) — not a setting.

New `IEmailService` methods (+ es/en templates + renderer entries), following the existing
typed-method-per-scenario pattern:

| Method | Recipients | Trigger |
|--------|-----------|---------|
| transfer requested → sender | Sender | `CreateRequestAsync` |
| transfer requested → team | tickets@ (link to admin detail) | `CreateRequestAsync` |
| transfer completed | Sender + Receiver | `ApproveAsync` |
| transfer cancelled | Sender + Receiver (incl. reason) | `RejectAsync` |

Each user-bound email uses the recipient's preferred culture; the team email uses the org default.
The Receiver is **not** emailed at request time (only on decision) to avoid raising expectations on a
request that may be cancelled.

## Admin detail page — `/Tickets/Admin/Transfers/Detail/{id}`

- Replace the "If you approve, this will happen: void → issue → TT emails…" block with manual
  instructions: *"Process this transfer in TicketTailor (void the original, reissue to the recipient).
  When done, mark it successful — the next sync updates the ticket records automatically."*
- Buttons: **Mark transfer successful** (POST Decide approve=true) and **Cancel transfer** (POST
  Decide approve=false, **reason required**). Keep **View order in TicketTailor**.
- Remove the RetryIssue form and the `<vc:ticket-transfer-timeline>` component (delete the component +
  its view). Drop `VendorStepsJson` from `TicketTransferDetailDto`.
- Reword the checked-in warning (no auto-void now): confirm with the Sender before processing.
- Keep the sender/receiver cards, ticket info, and audit-log component.

## Admin index page — `/Tickets/Admin/Transfers`

- Remove the **needs-attention** tab and `NeedsAttentionCount` (they keyed off vendor failures that no
  longer exist). Keep **pending** + **all** tabs.
- Keep the order-drift table (`GetOrderDriftAsync`) — issued-vs-valid drift is still a useful manual
  reconciliation aid and is independent of writeback.

## Dashboard — `Home/Dashboard.cshtml`

- **Header**: when the user holds a ticket, render a compact `<vc:ticket-stub>` beside the
  "Welcome, {name} 👋" greeting (plain header otherwise).
- **Tickets section**: held tickets render as `<vc:ticket-stub>` (pending stamp + Sender Cancel), with a
  single **"Transfer a ticket?"** link → `/Tickets/Transfers` replacing the per-attendee "Send" /
  "Ticket Transfers coming soon…" controls. Drop the `canSendTicketTransfer` per-row gating.

## Docs

- Rewrite `docs/features/tickets/ticket-transfer.md`: drop US-42.4/42.5/42.6 (vendor writeback,
  Option-C, sync resilience for writeback), the vendor branches of the state machine, and the bespoke
  Receiver Lookup Contract (now: standard `<vc:human-search>` + `AllowEmail`). Update freshness
  triggers (remove deleted files).
- Update `docs/sections/Tickets.md`: `TicketTransferRequest` data model (removed columns), lifecycle,
  routing (`/Tickets/Transfers`), and remove the vendor-writeback invariants/triggers.
- Update `memory/architecture/person-search.md` as above.

## Testing

- `TicketTransferService` tests: delete vendor-writeback cases (void/issue/retry/Option-C); add
  assertions that request/decision emails are sent to the right recipients; verify reject requires a
  reason and approve mutates no attendee rows.
- `ProfileApiController` tests: add the `allowEmail` exact-email branch (hit, miss, non-email query,
  flag off).
- Update any architecture/interface tests that referenced the removed members or the
  `ITicketVendorService` dependency on `TicketTransferService`.
- `<vc:ticket-stub>` rendering: pending stamp shown when a ticket has a pending outgoing transfer; void
  styling; empty/no-ticket case on the homepage header.

## File change map (indicative)

**Delete**
- `src/Humans.Domain/Enums/TicketTransferVendorResult.cs`
- `TicketTransferVendorStep` / `TicketTransferVendorStepKind` (value objects)
- `src/Humans.Web/.../TicketTransferTimeline` ViewComponent + view
- `src/Humans.Web/Views/TicketTransfer/Send.cshtml`

**Modify**
- `src/Humans.Domain/Entities/TicketTransferRequest.cs`
- `src/Humans.Application/Services/Tickets/TicketTransferService.cs`
- `src/Humans.Application/Interfaces/Tickets/ITicketTransferService.cs`
- `src/Humans.Application/DTOs` — `MyAttendeeRowDto`, `TicketTransferDetailDto`, new confirmation DTO
- `src/Humans.Application/Interfaces/Email/IEmailService.cs` + `OutboxEmailService` + renderer + templates
- `src/Humans.Web/Controllers/TicketTransferController.cs`
- `src/Humans.Web/Controllers/TicketTransferAdminController.cs` (index: drop needs-attention)
- `src/Humans.Web/Controllers/ProfileApiController.cs`
- `src/Humans.Web/ViewComponents/HumanSearchViewComponent.cs` + `Models/HumanSearchPickerViewModel.cs` + `Views/Shared/Components/HumanSearch/Default.cshtml`
- `src/Humans.Web/Views/TicketTransferAdmin/Detail.cshtml` + `Index.cshtml`
- `src/Humans.Web/Views/Home/Dashboard.cshtml` (header stub + tickets section)
- `src/Humans.Web/ViewComponents/TicketHoldingsViewComponent.cs` + its view (render stubs)
- `src/Humans.Web/Views/Profile/Index.cshtml` (ticket card → stubs)
- `src/Humans.Application/Services/Tickets/TicketQueryService.cs` (+ caching wrapper) — `TicketStubInfo` projection
- `src/Humans.Web/Views/Shared/Components/Human/*` is untouched; `<vc:human-search>` files as above
- `src/Humans.Domain/Constants/TicketConstants.cs` (tickets@ constant)
- migration under `src/Humans.Infrastructure/Migrations`
- docs as listed above

**Add**
- `src/Humans.Web/Views/TicketTransfer/Index.cshtml` (the wizard)
- `src/Humans.Web/ViewComponents/TicketStubViewComponent.cs` + `Views/Shared/Components/TicketStub/Default.cshtml`

## Open items for the implementation plan

- Exact wording + structure of the four email templates (subject + body, es/en).
- Whether the confirmation DTO/method lives on `ITicketTransferService` or a thinner read path.
- Localization keys for the new wizard + dashboard link.
- Source of the event label on the stub ("Elsewhere 2026") — active event name/year from
  `IShiftManagementService.GetActiveAsync`, or a constant.
- Homepage: confirm the header stub and the lower tickets-section stubs don't feel duplicative on the
  same page (e.g. header stub only when exactly one ticket, or it's purely a flourish) — settle against
  the rendered page.
