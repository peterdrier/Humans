# HiEvents — Target Shape

Written fresh each section-doctor run (Phase 3c), before any scan. History rows at the bottom.

## What the section does

When a 2027 ticket sale is set up to take its money through Hi.Events, Humans hands Hi.Events an
order it has already priced and named, and Hi.Events only runs the card payment on its own pay page.
Afterwards Humans asks Hi.Events what became of the order (paid, still waiting, slow bank payment
on its way, abandoned, cancelled, refunded, charged back), asks it to refund an exact amount, to
close an unpaid order, or to forget the buyer's personal details. Hi.Events pings Humans whenever an
order changes, so Humans looks again sooner than its own poll would. The ticketing team can test the
connection and one event's setup before selling, see the settings that are set or missing, and list
everything Hi.Events holds for an event to reconcile it against Humans' own books. Hi.Events never
writes to buyers and never sells on its own public page for these events.

## The shapes

| Shape | Members | Notes |
|---|---|---|
| Open a payment for a priced order | `CreateCheckoutAsync` | Reads the event's carrier products first; idempotent on the Humans order id; refuses any price, currency or total it cannot charge exactly |
| Ask what became of an order | `GetStatusAsync`, `CancelAsync` (reads before it acts) | One status mapping (`ToState`) serves status, cancel and the order list |
| Move money back | `RefundAsync` | Idempotent on the Humans refund id; a repeat answers the earlier refund's state |
| Forget a buyer | `ErasePersonalDataAsync` | Whole-order anonymise in the fork |
| Reconcile a period | `ListOrdersAsync`, `EventRefOf`, `GetPaymentLedgerAsync` (always null) | The vendor side of Tickets' reconciliation |
| Connect and check | `Setup`, `CheckAsync`, `IsConfigured` | Changes nothing (the checkout probe fails validation before any order is made); never throws |
| Hear about a change | `POST /HiEvents/Webhook` | Signature-checked nudge to Tickets, no state change here |
| Show up in admin | the "Hi.Events" admin sidebar row | Opens Tickets' backend page; the section contributes only the row |

## Structure

One adapter class behind Tickets' port, one settings type, one webhook controller with its static
signature reader, and the section registration. No tables, no views, no Contracts of its own: the
whole public face is the port Tickets owns. The structure the shapes imply is the one the section
has; the wire records stay private inside the adapter so the Hi.Events payload shape never leaks.

## Invariants

- Unconfigured means no checkout: `IsConfigured` needs an absolute http(s) API URL, a usable app URL
  and an API key (`src/Sections/Humans.HiEvents/Services/HiEventsSettings.cs:23`), and a malformed URL
  only switches the backend off, never throws while Tickets resolves backends
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:58`).
- No order is created in Hi.Events unless the event id is numeric
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:77`), no carrier is visible
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:81`), and each line finds exactly
  one carrier of its kind (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:424`).
- A retried create cannot make a second order: the Humans order id is the `external_reference`
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:89`).
- An order Hi.Events would charge differently is refused: wrong currency
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:110`) or a total other than the
  sum of the lines (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:116`).
- Hi.Events mails nobody: checkout sends `SendBuyerEmails: false`
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:97`) and refunds
  `NotifyBuyer: false` (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:149`).
- A retried refund cannot refund twice: the Humans refund id is the idempotency key
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:151`); a 422 is a refund that did
  not happen (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:156`); an unnamed or
  unknown refund answer throws so Tickets keeps it pending
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:166`,
  `src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:173`).
- A paid order or one with a payment on its way is never cancelled
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:184`,
  `src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:186`), and cancelling never
  refunds (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:193`).
- A payment on its way (`payment_processing_at` set) is `Processing` whatever the hold says
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:393`); an unknown order status or
  an unreadable `reserved_until` throws rather than guessing
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:397`,
  `src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:400`).
- A partial order list is never taken for the whole: a page without paging throws
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:346`), and a distinct count other
  than Hi.Events' total throws (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:361`).
- The webhook changes nothing and trusts nothing unsigned: 503 without a secret
  (`src/Sections/Humans.HiEvents/Controllers/HiEventsWebhookController.cs:29`), 401 on a bad signature
  (`src/Sections/Humans.HiEvents/Controllers/HiEventsWebhookController.cs:41`), a constant-time compare
  (`src/Sections/Humans.HiEvents/Services/HiEventsWebhookSignature.cs:12`), and only `order.*` events
  reach Tickets (`src/Sections/Humans.HiEvents/Services/HiEventsWebhookSignature.cs:26`).
- Secrets never show: `Setup` gives the key and secret no value
  (`src/Sections/Humans.HiEvents/Services/HiEventsCheckoutBackend.cs:223`).

## Seams

- **Fee and paid time.** The port carries `FeeMinor` and `PaidAt`; Hi.Events does not expose them,
  so both stay null until the fork does.
- **Payment ledger.** `GetPaymentLedgerAsync` answers null: Hi.Events takes payments through its own
  Stripe account, whose keys Humans does not hold.
- **Unpaid orders in reconciliation.** Hi.Events' order list leaves out `RESERVED` and `ABANDONED`
  orders, so the vendor side of reconciliation sees only orders that reached a payment or a close.

## Deliberately not done

- No per-event configuration in Humans: carrier products are found in Hi.Events at checkout.
- No mirroring of Humans' catalogue into Hi.Events, no promo codes, no Hi.Events Cloud support.
- No Contracts of its own; nothing outside the project names the adapter or its settings.
- No retry or backoff inside the adapter: Tickets owns retry and the poll.
- No paging of the product list: an event carries two carrier products, far under one page.

## Load-bearing weirdness

- **The fork, not stock Hi.Events.** Checkout, refund idempotency, anonymise, `payment_processing_at`
  and `total_disputed` exist only in `peterdrier/Hi.Events`; `CheckAsync` probes the checkout route
  with an empty body to tell the two apart.
- **Backend order id `{eventId}:{orderId}`.** Every Hi.Events order route is scoped to its event.
- **Erasure takes the whole order.** Hi.Events copies the buyer's email onto every attendee, so the
  person cannot be told apart from the others on the order.
- **Buyer name split 50 plus 50.** Hi.Events' order name columns are 50 characters each; the attendee
  line carries the full legal name.
- **The hidden-carrier rule.** The pay page needs the event live, and a visible carrier would sell on
  the event's public page at its base price; the fork's checkout ignores the flag.
- **`AWAITING_OFFLINE_PAYMENT` reads as Pending.** Offline payment must be off; if it is on, the
  order is still unpaid.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-11-22 | First doctoring: webhook and money-guard tests, offline-payment and lifetime doc fixes, one carrier rule | pending |
