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

## Concepts

- The **port** is `Humans.Tickets.Contracts.ITicketCheckoutBackend`. Tickets owns it; this section implements it and
  publishes nothing.
- The **fork**: `peterdrier/Hi.Events` adds `POST /api/events/{event_id}/orders/checkout`, which creates a reserved
  order at the prices given, with buyer and attendee names filled in, and returns `payment_link_url`. It also accepts
  `idempotency_key` on `POST .../orders/{order_id}/refund`. Stock Hi.Events has neither.
- A **backend order id** is `{eventId}:{orderId}`: every Hi.Events order route is scoped to its event.
- **Settings**, all owned here: `HiEvents:ApiUrl` (ends in `/api`); `HiEvents:AppUrl` (the web app, for the order's
  admin link; defaults to `ApiUrl` without `/api`); per Hi.Events event id,
  `HiEvents:Events:{id}:TicketProductId` and `HiEvents:Events:{id}:DonationProductId`, so several events sell at once;
  and the environment variables `HIEVENTS_API_KEY` (a Hi.Events API key whose owner can
  edit the event, see financials and issue refunds) and `HIEVENTS_WEBHOOK_SECRET`.

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
- Checkout: `BackendEventRef` must be the numeric Hi.Events event id, else `TicketCheckoutException`; so is an event with no `TicketProductId` in `HiEvents:Events`. The Humans order id
  (`N` format) is the `external_reference`, so a retried create returns the same Hi.Events order. Ticket lines go to
  the event's `TicketProductId` with the line's attendee name; donation lines go to its `DonationProductId`, and a donation with none
  set throws `TicketCheckoutException`. A 409 or 422 from Hi.Events, a currency other than the request's, or a total
  other than the sum of the lines throws `TicketCheckoutException`. The products must carry no taxes or fees in
  Hi.Events.
- Checkout returns `AdminUrl`, the order in Hi.Events' event order list, so the ticketing team can jump to it.
- Status: `COMPLETED` with `PAYMENT_RECEIVED` is Paid; other `COMPLETED` is Failed; `CANCELLED` is Cancelled;
  `ABANDONED`, or `RESERVED` past `reserved_until`, is Expired; other `RESERVED` is Pending (a failed card attempt can
  be retried on the same link). `RefundedMinor` is `total_refunded`. Fee and paid time are not exposed by Hi.Events and
  stay null.
- Refund: always `Pending` on acceptance, because Hi.Events refunds through Stripe asynchronously; the amount shows in
  `RefundedMinor` once Stripe confirms. The Humans refund id is the idempotency key, so a retry after a completed
  refund, or while that same refund is still pending in Stripe, refunds nothing and is accepted. Every 422 is a refund
  that did not happen and answers `Failed` with Hi.Events' message. `RefundFailed` is Hi.Events' `REFUND_FAILED`, set
  when Stripe fails a refund it had accepted.
- Late payment: a payment that lands after Humans cancelled the checkout, or after it expired, is kept by the fork (it
  never auto-refunds orders created here) and reads as `Paid`.
- Cancel: a no-op on a cancelled order, `TicketCheckoutException` on a paid one, otherwise Hi.Events' cancel without
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

## Personal data and erasure

Hi.Events holds each buyer's name and email and each attendee's legal name for orders created here.
`ErasePersonalDataAsync` calls the fork's `POST .../orders/{order_id}/anonymize`, which scrubs the buyer and every
attendee on the order and keeps amounts, statuses and dates. The whole order goes, not just the named person, because
Hi.Events copies the buyer's email onto every attendee row, so the person cannot be told apart from the others; Humans
keeps the names it needs. A failure throws, so Tickets retries before tombstoning its own copy.
