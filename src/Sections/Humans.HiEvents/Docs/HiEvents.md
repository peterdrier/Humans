<!-- freshness:triggers
  src/Sections/Humans.HiEvents/**
  src/Sections/Humans.Tickets.Contracts/ITicketCheckoutBackend.cs
  tests/Humans.HiEvents.Tests/**
-->
<!-- freshness:flag-on-change
  The wire mapping and status table in HiEventsCheckoutBackend.cs, and the signature check in the webhook controller.
-->

# HiEvents — Section Invariants

The Hi.Events option for 2027 ticket checkout: one implementation of Tickets' `ITicketCheckoutBackend` port (key
`hievents`), plus the webhook that tells Tickets an order changed. Hi.Events only takes the payment; Humans owns the
price, the named ticket and the QR code.

## Purpose

Hi.Events is an open-source event ticketing platform. This section lets Humans use the Nobodies fork of it (`peterdrier/Hi.Events`, `docker/coolify/` there for Coolify) as a payment desk: it creates a reserved Hi.Events order at Humans' exact prices, sends the buyer to Hi.Events' Stripe pay page, and reports back what Hi.Events holds.

**What good looks like.** A Humans order and its Hi.Events order always agree on amount, payment and refund state. A retried create or refund never doubles. Hi.Events sends buyers nothing, and nothing sells on its public event page.

**Deliberate choices.** A small fork patch (checkout endpoint, refund idempotency, anonymize) instead of per-person promo codes, since stock Hi.Events takes a caller price only for donations. One hidden `TICKET` and one hidden `GENERAL` carrier product per event instead of mirroring Humans' catalogue. Erasure anonymizes the whole order, because Hi.Events copies the buyer's email onto every attendee.

**Out of scope.** Hi.Events Cloud (it lacks the fork's endpoint), Hi.Events' own catalogue, promo codes, seating, box office, check-in and buyer mail; any per-event setting in Humans beyond the event id.


**Admin.** The section's only page is its admin sidebar row, "Hi.Events" (`TicketsBackendAdminNav`, `TicketsPolicies.Read`), which opens Tickets' status page for key `hievents` at `/Tickets/Admin/Backends/hievents`: settings, connection test, orders, stuck orders, unsettled refunds and a link to reconciliation. The data is Tickets'; this section contributes only the row.

## Concepts

- The **port** is `Humans.Tickets.Contracts.ITicketCheckoutBackend`. Tickets owns it; this section implements it and
  publishes nothing.
- The **fork**: `peterdrier/Hi.Events` adds `POST /api/events/{event_id}/orders/checkout`, which creates a reserved
  order at the prices given, with buyer and attendee names filled in, and returns `payment_link_url`. It also accepts
  `idempotency_key` on `POST .../orders/{order_id}/refund`. Stock Hi.Events has neither.
- A **backend order id** is `{eventId}:{orderId}`: every Hi.Events order route is scoped to its event.
- **Settings**, all owned here: `HiEvents:ApiUrl` (ends in `/api`); `HiEvents:AppUrl` (the web app, for the order's
  admin link; defaults to `ApiUrl` without `/api`; both must be absolute http(s), or the backend reads as unconfigured and
  Tickets opens no checkout on it); and the environment variables `HIEVENTS_API_KEY` (a Hi.Events API key whose owner can
  edit the event, see financials and issue refunds) and `HIEVENTS_WEBHOOK_SECRET`. Only infrastructure lives in
  configuration: there is no per-event mapping to keep, because each event's carrier products are found in Hi.Events.
- **Carrier products**: Humans owns the catalogue and prices every line, so each Hi.Events event needs exactly one
  product of type `TICKET` (ticket lines) and one of type `GENERAL` (donation lines), neither with taxes or fees, and
  both hidden. The event must be live for the pay page to work, and a visible carrier would sell on the event's public
  page at its base price, outside Humans; checkout refuses a visible carrier. The fork's checkout ignores the flag.

## Data Model

None — the section owns no tables.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Tickets | Resolves the backend by key and calls the port; an event is set up at `/Tickets/Admin/Sales` with backend key `hievents` and the Hi.Events event id |
| Hi.Events | Posts signed webhooks to `/HiEvents/Webhook` |
| Any human | None — the section has no views; the webhook is anonymous and signature-checked |

## Invariants

- `IsConfigured` is true only with an API URL and an API key.
- Checkout: `BackendEventRef` must be the numeric Hi.Events event id, else `TicketCheckoutException`. The Humans order id
  (`N` format) is the `external_reference`, so a retried create returns the same Hi.Events order. The event's products are read
  first: ticket lines go to its one `TICKET` product with the line's attendee name, donation lines to its one `GENERAL`
  product, and none or several of the kind a line needs throws `TicketCheckoutException` before any order is made. A 409 or 422 from Hi.Events, a currency other than the request's, or a total
  other than the sum of the lines throws `TicketCheckoutException`. The products must carry no taxes or fees in
  Hi.Events.
- Checkout returns `AdminUrl`, the order in Hi.Events' event order list, so the ticketing team can jump to it.
- Status: `COMPLETED` with `PAYMENT_RECEIVED` is Paid; other `COMPLETED` is Failed; `CANCELLED` is Cancelled;
  Processing as below; otherwise `ABANDONED`, or `RESERVED` past `reserved_until`, is Expired; other `RESERVED` is Pending (a failed card attempt can
  be retried on the same link). `RefundedMinor` is `total_refunded`. Fee and paid time are not exposed by Hi.Events and
  stay null.
- Refund: the Humans refund id is the idempotency key. Hi.Events answers with the Stripe refund made under it
  (`BackendRefundId`) and its state: `succeeded` is `Succeeded`, `pending` or `requires_action` is `Pending`, `failed`
  or `canceled` is `Failed`. A repeat of the id never refunds again, whatever that refund's state, so Tickets repeats it
  to read a refund's own state. Every 422 is a refund that did not happen and answers `Failed` with Hi.Events' message.
  No answer, or an accepted refund Hi.Events does not name, throws. `RefundFailed` is Hi.Events' `REFUND_FAILED`, set
  when Stripe fails a refund it had accepted.
- Late payment: a payment that lands after Humans cancelled the checkout, or after it expired, is kept by the fork (it
  never auto-refunds orders created here) and reads as `Paid`.
- `Processing`: a `RESERVED` or `ABANDONED` order with `payment_processing_at` set, whatever `reserved_until` says. The
  fork sets it when Stripe reports a payment that settles later (`payment_intent.processing`, a SEPA debit), or when the
  pay page's return check finds one, and sends the buyer on to `ReturnUrl`; it clears it when the payment fails (the
  order reads Pending or Expired again) or succeeds (Paid). Tickets never cancels a Processing order at its hold.
- `DisputedMinor` is the fork's `total_disputed`: what Stripe chargebacks on the order's payments hold back (a dispute
  counts until won; an inquiry, `warning_*`, and a `prevented` one, which Stripe settles as a refund, take nothing), in
  `GetStatusAsync` and `ListOrdersAsync` alike. On every `charge.dispute.*` event the fork re-lists all disputes on the
  order's payments from Stripe and sends `order.updated`, which nudges Tickets.
- The fork's Stripe webhook must carry `payment_intent.processing` and the `charge.dispute.*` events, or neither state
  reaches Humans.
- Cancel: a no-op on a cancelled order, `TicketCheckoutException` on a paid or Processing one, otherwise Hi.Events' cancel without
  refund.
- Webhook: 503 without a secret, 401 on a bad signature, 200 and no call for anything but `order.*`.
- After paying, Hi.Events' payment return page sends the buyer to `CheckoutRequest.ReturnUrl`.
- Hi.Events sends the buyer and attendees no email for these orders (`send_buyer_emails: false`, refunds with
  `notify_buyer: false`); Humans owns the ticket, receipt and refund emails. Organizer and team notices in Hi.Events
  still go out.
- Offline payment must be off on every Hi.Events event Humans sells through; an order moved to
  `AWAITING_OFFLINE_PAYMENT` reads as `Pending` until it expires.
- A reserved order whose `reserved_until` does not parse is an error, not `Pending`.
- A malformed `HiEvents:ApiUrl` only makes this backend unconfigured; it never throws while Tickets resolves backends.
- Buyer names go in 50 characters of first name and 50 of last name, split at a space; attendee lines carry the full
  legal name (Hi.Events allows 255 on this endpoint).
- `Setup` shows the URLs and only set or missing for the key and secret; an API host on `hi.events` reads as Hi.Events Cloud, unsupported because it lacks the fork's checkout endpoint, and never passes the check. `CheckAsync` reads `users/me` (a body without a user, such as the web app's HTML, fails), the event's status and products, then posts an empty body to the checkout endpoint: the fork answers 422 before creating anything, stock Hi.Events 404 or 405.
- Backend setup checks propagate caller cancellation instead of reporting a vendor failure; independent transport timeouts remain diagnostic findings.
- `ListOrdersAsync` pages through an event's orders by `created_at` (100 a page, to `meta.last_page`), reading the Humans order id from `external_reference` and counting attendees as tickets. It throws when the distinct orders read differ from `meta.total`, since ties in a second can move across pages. The list leaves out `RESERVED` and `ABANDONED` orders. It needs the key's user to see financials, as refunds already do. `EventRefOf` reads the event id from the order id. `GetPaymentLedgerAsync` answers null: Hi.Events takes payments through its own Stripe account, so its fees and payouts are not reconciled in Humans.

## Personal data and erasure

Hi.Events holds each buyer's name and email and each attendee's legal name for orders created here.
`ErasePersonalDataAsync` calls the fork's `POST .../orders/{order_id}/anonymize`, which scrubs the buyer and every
attendee on the order (soft-deleted rows too, using Hi.Events' own account-deletion anonymiser), the buyer's saved
Stripe customer row and the webhook delivery logs that carried the order, and keeps amounts, statuses and dates. The
customer record inside Stripe itself stays: Stripe holds it as payment processor. The whole order goes, not just the named person, because
Hi.Events copies the buyer's email onto every attendee row, so the person cannot be told apart from the others; Humans
keeps the names it needs. A failure throws, so Tickets retries before tombstoning its own copy.

## Negative Access Rules

- Code outside this project **cannot** name `HiEventsCheckoutBackend` or `HiEventsSettings`.
- The webhook **cannot** change any state itself; it only tells Tickets an order changed, and Tickets re-reads it.
- A buyer **cannot** buy through Hi.Events' public event page: checkout refuses a visible carrier product.

## Triggers

- A signed `order.*` webhook calls `ITicketCheckoutEvents.CheckoutChangedAsync("hievents", "{eventId}:{orderId}")`.

## Cross-Section Dependencies

- **Tickets**: implements `ITicketCheckoutBackend`, calls `ITicketCheckoutEvents`, both from the `Humans.Tickets.Contracts` leaf.
- **Base**: configuration registry.

## Architecture

**Owning services:** `HiEventsCheckoutBackend`
**Owned tables:** None, adapter over Tickets' port.
**Status:** (A) Migrated, the adapter shape: no tables, an empty `Contracts/`, one webhook controller.
