<!-- freshness:triggers
  src/Sections/Humans.Pretix/**
  src/Sections/Humans.Tickets.Contracts/ITicketingPlatform.cs
  tests/Humans.Pretix.Tests/**
  deploy/pretix/**
  src/Sections/Humans.Tickets.Contracts/TicketsPlatformAdminNav.cs
-->
<!-- freshness:flag-on-change
  The platform order id and order code scheme, the price check, refund idempotency, and the webhook's trust model.
-->

# Pretix — Section Invariants

The pretix ticketing platform behind Tickets' `ITicketingPlatform` port: Humans sets the price, pretix takes the payment.

## Purpose

pretix is an open-source ticketing system, self-hosted (`deploy/pretix/` for Coolify) or rented as pretix Hosted. This section lets Humans use stock pretix as a payment desk: it turns a Tickets order into a pretix order at Humans' exact prices, sends the buyer to pretix's pay page, and reports back what pretix holds.

**What good looks like.** A Humans order and its pretix order always agree on amount, payment and refund state. A retry never makes a second order or a second refund. Nothing in pretix sells on its own: waves, classes, capacity and buyer email stay in Humans.

**Deliberate choices.** Stock pretix, no fork. Two free-price carrier items per event instead of mirroring Humans' catalogue. No names sent to pretix. The webhook is a nudge, never trusted for state. Refunds go through the API, the ticketing platform exception in `memory/architecture/stripe-money-out.md`, and an exact negative fee stops pretix asking the buyer for refunded money again.

**Out of scope.** pretix's own shop, vouchers, seating, check-in app and mail to buyers; pretixSCAN at the gate (Gate's call); any per-event setting in Humans beyond the event slug.

**Admin.** The section's only page is its admin sidebar row, "pretix" (`TicketsPlatformAdminNav`, `TicketsPolicies.Read`), which opens Tickets' status page for key `pretix` at `/Tickets/Admin/Platforms/pretix`: settings, connection test, orders, stuck orders, unsettled refunds and a link to reconciliation. The data is Tickets'; this section contributes only the row.

## Concepts

- The **port** is `Humans.Tickets.Contracts.ITicketingPlatform` (key `pretix`). Tickets owns it; this section implements it and publishes nothing.
- A **platform order id** is `{event slug}/{order code}`. pretix addresses an order by both; the event slug arrives per order as `PlatformOrderRequest.PlatformEventRef`.
- The **order code** is 16 symbols from `A-Z2-9` without `I` and `O`, taken from a SHA-256 of the Humans order id. Same order, same code.
- The **carrier items** are two "free price" products in each pretix event, found per order by internal name: `humans-ticket` (admission, taxed like a ticket) and `humans-donation` (no admission, no tax). They carry Humans' prices and nothing else; waves, classes and capacity stay in Humans. An event missing either throws `TicketingPlatformException` before any order is made.
- The **pay link** is the order's pay-method page with `return_url`, handled by pretix's bundled `returnurl` plugin, so the buyer lands back on `PlatformOrderRequest.ReturnUrl` after paying.

## Data Model

None. The section owns no tables. pretix's order is the record of the payment; Tickets owns the Humans order.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Tickets | Resolves the port by key and calls it |
| pretix | Posts order notifications to `/Pretix/Webhook?secret={secret}` |
| Any human | None; the buyer only follows the pay link to pretix |

## Invariants

- Reporting-category classification uses `PaymentLedgerEntry.KindOf`, shared by all three ticketing adapters.

- `CreateOrderAsync` creates the order through `POST .../orders/` with `force` (Humans owns capacity), `send_email: false` (Humans sends the ticket mail), `expires` set to `ExpiresAt`, the Humans order id in `api_meta.humans_order_id`, and one position per line at the line's exact price.
- A repeat create with the same order id finds the existing order by code and returns its pay link; a code held by another `humans_order_id` throws `TicketingPlatformException`.
- The adapter never changes a price. Each position's price and tax rate must equal its line's amount and `VatRate`, and a fresh order's total must equal the lines' sum; on a mismatch a just-created order is cancelled and `TicketingPlatformException` is thrown. A currency the event does not sell throws before anything is created.
- A buyer language the event does not offer is dropped and the order retried, so pretix uses the event default.
- Status maps pretix `n/p/e/c` to Pending/Paid/Expired/Cancelled, except that money decides: a pending or expired order its confirmed payments cover, or a cancelled order holding unrefunded money, is Paid. A pending order with a payment in pretix's `pending` state (a SEPA debit settling) is Processing; a bank transfer not yet sent stays `created` and the order stays Pending. pretix keeps a payment confirmed after expiry or cancel (a slow iDEAL, Klarna or 3DS) but cannot mark the order paid, since the carrier items have no quota; the port says such a payment is reported, and Tickets issues and flags it. Money that has gone back counts from the moment pretix records it: refunds in state `done`, and `external` ones (a Stripe dashboard refund or a chargeback, waiting for a person to process them in pretix). For cover and for refund retries, a refund still on its way (`created`, `transit`) counts as gone too. Of those, a refund with source `external` whose `details.id` is a Stripe dispute (`dp_`/`du_`) is `ChargedBackMinor`; every other one is `RefundedMinor`, however issued. pretix never updates a recorded chargeback, so a won dispute or an inquiry stays charged back until a person cancels that refund in pretix. pretix allows the cancel only while the refund is still `external`: once a person processes it, or pretix completes it by itself (when it exactly settles an overpayment, such as a late payment on a cancelled order), it stays charged back for good: nothing in Humans clears it today. The team's rule: leave a chargeback unprocessed in pretix until the dispute is final, cancel it if won, process it if lost. An inquiry closed by a full refund reads as both refunded and charged back (net twice the amount) until its dispute record is cancelled.
- `FeeMinor` is what Stripe kept on a paid order's payment, read through `IStripeService` by the PaymentIntent id in the payment's `details.id`; null while the account is not configured, for a payment that is not a PaymentIntent, or when Stripe cannot be read, so the fee never holds up a paid order. The buyer pays exactly the lines, so `bootstrap.py` sets every payment method's fee to zero; a payment surcharge found on an order is logged as an error, not thrown, so a real payment still reaches Humans.
- `RefundFailed` is true while the newest refund on the order that is not `external` is `failed` or `canceled` in pretix; a later such refund clears it, a Stripe dashboard refund or chargeback does not.
- Erase clears the order's email and phone when the email is the buyer's, and the holder name and email (`attendee_name`, `attendee_email`) on its positions (the buyer's order, or positions carrying that email); amounts stay. Humans sends no names, so this mostly clears the email. pretix logs the old email in the order's history (`pretix.event.order.contact.changed`), which its API cannot delete, and its Stripe plugin keeps the payment's billing details on the payment; both go only when pretix's data shredder runs for the event after it ends (peterdrier/Humans#2623). A ticket holder's erasure waits until 30 days after the event, so a shred run before then does not cover it.
- Refund first lowers the pretix order total by the amount, as a negative `other` fee with internal type `humans-refund:{RefundId}`, then calls `payments/{id}/refund/` on a confirmed payment with enough left, with `mark_canceled: false` and the same marker as comment. Without the fee pretix reopens the paid order and its pay page asks the buyer for the refunded amount again. A repeat with the same `RefundId` answers that refund's current state and platform id and never refunds again; when it has since failed or been canceled, the marked fee comes off. A fee already carrying the marker is reused.
- pretix's 400 "refund not available for this payment method" is `ManualRequired` with the order's pretix control page; the fee stays, so pretix shows exactly that amount as owed back. Any other 400 (provider error, bad amount or payment state) is `Failed`, and the fee comes off again, also one left by an earlier unanswered attempt, unless a refund made by hand has already paid that amount out. No answer or a pretix 5xx throws, so Tickets keeps the refund pending and retries it under the same id, which reuses the fee and any refund already made; any other refusal (401, 403, 404) is `Failed` with the fee taken off on the same terms. Asking for more than any payment has left is `Failed` without a call.
- pretix holds no names: an order carries only the buyer's email, the Humans order id and the amounts.
- Cancel throws on an order that reads as Paid or Processing, is a no-op on a cancelled one, and cancels pending and expired ones (pretix still takes a late payment on an expired order). Tickets calls it when its hold lapses.
- Paid orders are re-read only when the webhook fires, so `bootstrap.py` subscribes `refund.created.externally` (how a dashboard refund or a chargeback arrives) and `refund.canceled` (how it clears) besides the order and refund actions, and adds missing listeners to an existing hook.
- The webhook is a nudge only: a constant-time match of the URL secret against `PRETIX_WEBHOOK_SECRET`, 503 while unset, 404 on a mismatch, 200 and nothing for another organizer. Tickets re-reads the order from pretix, so a forged call costs one read.
- Several Humans events can sell at once, each on its own pretix event; nothing in the settings is per event.
- The Stripe account whose keys are set in pretix's Stripe settings is the Stripe section's `Stripe:MirrorAccounts` entry named `Tickets` (its `acct_` id, and a read-only key: its own through `KeyVariable`, or the org read key), with Balance, Balance transactions, Payouts, PaymentIntents, Charges, Refunds and Disputes read; it moves no money, and the platforms page Test reads each scope the reconciliation needs.
- The platform is live in every environment once configured: `BaseUrl` must be absolute HTTP(S), `Organizer` nonblank, and `PRETIX_API_TOKEN` nonblank and valid in an authorization header. Invalid settings make `IsConfigured` false; every order call throws `TicketingPlatformException` before HTTP, the ledger returns null, and `CheckAsync` reports configuration before calling Stripe or pretix. `Setup` still answers; diagnostic refusals include no token value.
- `Setup` shows `BaseUrl` and `Organizer` and only set or missing for the token and secret; a `BaseUrl` on `pretix.eu` reads as pretix Hosted, which the adapter serves unchanged. `CheckAsync` only reads: the organizer, then for an event slug its currency, live and test mode, and exactly one active item per carrier name; a test-mode event and an id that is not a slug fail.
- Platform setup checks propagate caller cancellation instead of reporting a vendor failure; independent transport timeouts remain diagnostic findings.
- `ListOrdersAsync` pages through an event's orders created in the period (`created_since`/`created_before`), counts positions on any `humans-ticket` item (inactive ones too) as tickets, and reports the total with the adapter's refund fees added back, so it is what the order charged. An order whose confirmed and refunded payments cover that total reads Paid, whatever pretix's status (pretix cancels or reopens an order once a refund is processed, and keeps a late-paid one expired, while Tickets keeps it Paid), and reports what those payments received instead; refunded and charged back are the same as `GetStatusAsync` reports, so reconciliation and the order agree. `EventRefOf` reads the slug from the order id. `GetPaymentLedgerAsync` reads the shared `Tickets` Stripe account through `IStripeService` (null while not configured; a Stripe refusal is a `TicketingPlatformException`): its balance transactions and payouts created in the period. pretix puts the order `code` on each PaymentIntent and describes it as `{EVENT SLUG}-{code}` in capitals, so the slug is found among the organizer's events (read once a call) and the movement's Humans order is that pretix order's `api_meta`; a payment on another organizer's event, or an order Humans did not open, names none.

- Checkout creation, setup checks and reconciliation read all carrier-item pages. Item, order and organizer-event pagination share one reader that rejects repeated resolved page URLs and stops at 500 pages instead of looping indefinitely; an incomplete traversal throws rather than returning partial results.

- API requests, including item, order and organizer-event pagination links, must stay on the configured `BaseUrl` origin (scheme, host and port). A different origin is refused before attaching the organizer token or sending a request; relative and same-origin absolute links remain supported.

## Negative Access Rules

- Code outside this project **cannot** name `PretixPlatform` or `PretixSettings`.
- The webhook **cannot** change any state itself; it only tells Tickets an order changed.
- A pretix team token **can** refund (pretix has one `event.orders:write` permission for create and refund). `bootstrap.py` gives the token order read and write on the events Humans sells and nothing else.

## Triggers

- A pretix webhook calls `ITicketingPlatformEvents.PlatformOrderChangedAsync("pretix", "{event}/{code}")`.

## Cross-Section Dependencies

- **Tickets**: implements `ITicketingPlatform`, calls `ITicketingPlatformEvents`, both from the `Humans.Tickets.Contracts` leaf.
- **Stripe**: `IStripeService`'s account reads of the `Tickets` mirror account (ledger, fee, key check).
- **Base**: configuration registry.

## pretix setup

Per event: the two carrier items, the `returnurl` plugin with Humans' return URL prefix, the Stripe plugin with card, iDEAL and Klarna enabled and no method fees, a webhook for the order actions to `https://<humans>/Pretix/Webhook?secret=<secret>` (in the query, which request logging leaves out), and ticket download and every buyer mail off (`send_email: false` covers only the create call; a payment confirmed later by Stripe sends pretix's paid mail unless the event's `mail_sales_channel_placed_paid` is empty, which `bootstrap.py` sets). Hosting: `deploy/pretix/` in the repo root.

## Architecture

**Owning services:** `PretixPlatform`
**Owned tables:** None, adapter over Tickets' port.
**Status:** (A) Migrated, the adapter shape: plain `Microsoft.NET.Sdk`, no tables, an empty `Contracts/`, one webhook controller.

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| IPretixService (none) | — | Not cross-section-consumed; Tickets reaches the section through its own port |
| IPretixWrite (none) | — | None |

### For (A) Migrated sections

- No repository and no `DbContext`: pretix's API is the store.
- **Decorator decision:** no caching decorator. Every call reads pretix, which is the truth for the order.
- **Cross-section calls:** `ITicketingPlatformEvents` (the webhook's nudge) and `TicketsPlatformAdminNav` (the admin row), both from the `Humans.Tickets.Contracts` leaf; `IStripeService` (the Stripe account reads).
