<!-- freshness:triggers
  src/Sections/Humans.BoxOffice/**
  src/Sections/Humans.Tickets.Contracts/ITicketingPlatform.cs
  src/Sections/Humans.Tickets.Contracts/TicketsPlatformAdminNav.cs
  src/Sections/Humans.Tickets/Views/Tickets/Admin/Sales/Index.cshtml
  memory/architecture/stripe-money-out.md
  tests/Humans.BoxOffice.Tests/**
-->
<!-- freshness:flag-on-change
  The session status mapping, refund idempotency and cap, and the webhook's event list and trust model.
-->

# BoxOffice — Section Invariants

Vocabulary: [`CONTEXT.md`](CONTEXT.md).

Humans' own ticketing platform: one implementation of Tickets' `ITicketingPlatform` port, key `boxoffice`, that
takes payment through Stripe hosted Checkout and refunds through the Stripe API. It owns no tables and no Tickets data:
Tickets owns the order, the price and the named ticket; this section only turns them into a Stripe Checkout Session
and reads it back. Spec: `src/Sections/Humans.Tickets/Docs/features/checkout.md`.

## Purpose

Box Office is the option with no outside ticketing platform at all: Humans sells, and Stripe only takes the card, Klarna or
iDEAL payment on its hosted page and refunds it. It needs no container to run, only a Stripe account kept for tickets.

**What good looks like.** Every Checkout Session matches one Humans order to the cent, every state Stripe can put a
payment in (3-D Secure, a debit settling for days, a failed debit, a chargeback, a refund made in the dashboard)
reaches Tickets, and a repeated call never charges or refunds twice. What Stripe charged, refunded, kept as fees
and paid out to the bank reconciles against Humans' orders per period, with a drill-down where the two differ.

**Deliberate choices.** The shared `STRIPE_TICKETS_KEY` and its own webhook on the ticket
Stripe account, apart from the Store's Stripe integration. API refunds are allowed here as the one money-out exception.
This section stores nothing: every order, refund, ticket and timeline entry lives in Tickets' tables
(`ticket_checkout_orders`, `ticket_checkout_refunds`, `ticket_checkout_tickets`, `ticket_checkout_order_events`), the
same for all three platforms.

**Out of scope.** Stripe products, prices, coupons and invoices; erasing data inside
Stripe, which keeps it as the payment processor.

**Admin.** The section's only page is its admin sidebar row, "Box Office" (`TicketsPlatformAdminNav`, `TicketsPolicies.Read`), which opens Tickets' status page for key `boxoffice` at `/Tickets/Admin/Platforms/boxoffice`: settings, connection test, orders, stuck orders, unsettled refunds and a link to reconciliation. The data is Tickets'; this section contributes only the row.

## Concepts

- The platform order id is the Checkout Session id. Each order line becomes one Stripe line at exactly its amount
  (zero lines are left out); the order id keys the Stripe idempotency key (`humans_ticket_checkout_<order id>`) and is the
  `client_reference_id` and the `humans_ticket_order_id` metadata on the session and its PaymentIntent.
- Payment methods are whatever the Stripe account enables for Checkout (card, Klarna, iDEAL). The locale follows the
  buyer's culture where Stripe has it; Catalan falls back to Stripe's choice.
- Status is read from the session with its PaymentIntent and latest charge: `complete` + `paid` (or
  `no_payment_required`, a session made outside Humans) is Paid, `expired` is Expired, a completed session whose PaymentIntent is `canceled` or back at `requires_payment_method`
  (an async debit that failed) is Failed, any other completed session is Processing (a SEPA debit or bank transfer
  still settling, which Stripe no longer lets anyone expire), and an open session is Pending. 3-D Secure and other
  buyer actions happen on Stripe's page while the session is open. A charge with Stripe disputes reports the amount of
  those that hold money (not `won`, not a `warning_*` inquiry) as `ChargedBackMinor`.
  Fee is the balance transaction's total fee, read through `IStripeService.GetAccountPaymentFeeAsync` on the `Tickets` mirror account (unknown is null); refunded total is the sum of the PaymentIntent's `succeeded` refunds (Stripe's `amount_refunded` also counts pending ones), so dashboard refunds are
  seen too. `RefundFailed` is set when one of Humans' refunds on it failed or was cancelled; Tickets then repeats each pending refund by id, and a repeat answers that refund's own state.
- A refund request first looks for a refund carrying its Humans refund id and answers with that one's state, so a
  repeat after a lost answer is never refused by the cap.

## Data Model

None. The section owns no tables. The Checkout Session is the record of the payment; Tickets owns the Humans order.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Tickets | Resolves the port by key `boxoffice` and calls it |
| Stripe | Posts signed events to `/BoxOffice/StripeWebhook` |
| Any human | None; the buyer only follows the Checkout link to Stripe |

## Invariants

- Refunds, disputes and webhook session lookups consume every Stripe page before returning. Later-page failures or cancellation reject the read without returning partial totals or session IDs; refund retries find their original refund beyond the first page.

- Native status and order lists require explicit EUR on the session, expanded charge, refunds and money-holding disputes before using amounts. Unsupported or missing currency rejects the read, including the whole order list. Refunds validate the session and charge before issuing any remote mutation.

- Stripe reporting accepts explicit EUR only. A movement, payout or summed payout content with another or missing currency rejects the ledger; an unverified fee remains unknown.

- The checkout API key must be nonempty and contain no whitespace, matching Stripe.net client validation. An unusable key makes the platform unconfigured; order operations refuse through `TicketingPlatformException` before SDK construction, reporting returns null, and connection checks report configuration without Stripe calls or key text. Webhook signing secrets retain their separate validation.

- Charges EUR only; a non-EUR request, a negative line or a non-positive total throws `TicketingPlatformException` before
  any Stripe call. The adapter never changes an amount.
- Session expiry is clamped to 31 minutes .. 23 hours from now, inside Stripe's 30 minutes .. 24 hours.
- A refund first searches every page for its Humans refund ID, regardless of age. If no matching refund exists, a new POST requires the persisted request time to be less than 24 hours old and not in the future. A missing or expired timestamp throws and keeps Tickets' refund pending for operator reconciliation; it never releases the reservation or reuses an expired key. [Stripe limits unresolved retries to 24 hours](https://docs.stripe.com/error-low-level#post-requests).
- A refund error is final only for a recognized request, authentication, permission or card rejection with HTTP 400–404. Server, rate-limit, idempotency and unknown errors throw, leaving Tickets' refund pending and its amount reserved until reconciliation resolves the original refund ID.
- A refund goes only against the session's own PaymentIntent, never above what the charge still holds (amount minus
  `amount_refunded`), with the idempotency key `humans_ticket_refund_<refund id>`. Tickets' own cap (ticket lines net of earlier
  refunds) sits in front. This is the one money-out exception in `memory/architecture/stripe-money-out.md`.
- The webhook (`POST /BoxOffice/StripeWebhook`, anonymous, Stripe-signature verified, 503 while the Tickets key or webhook secret is
  unset) only names sessions to `ITicketingPlatformEvents`, by the event's object: a Checkout Session names itself; a
  charge, refund or dispute names the sessions of its PaymentIntent; anything else names none. An invalid signature
  answers 400, so a wrong secret shows as failed deliveries in Stripe.
- Cancelling expires the session; Stripe refusing that is an error only when the session is complete and not failed.
- `Setup` shows the shared Tickets key and webhook secret only as set or missing, and test or live mode from the key prefix. `CheckAsync` delegates to `IStripeService.CheckAccountAsync`: one Checkout Session with the Tickets key, and balance transaction, payout and PaymentIntent probes with the `Tickets` mirror account read key. It also reports a missing webhook secret; it cannot test the refund permission without refunding.
- Backend setup checks propagate caller cancellation instead of reporting a vendor failure; independent transport timeouts remain diagnostic findings.
- `ListOrdersAsync` lists every Checkout Session created in the period, whatever made it, since the account sells tickets only. Each session carries `humans_ticket_count` (ticket lines, free ones included) for reconciliation; one without it has no count. Refunds are read only for a charge that shows some. `EventRefOf` is always null.
- `GetPaymentLedgerAsync` reads the `Tickets` account through `IStripeService`, returning null while checkout or reporting is unconfigured and wrapping a Stripe refusal in `TicketingPlatformException`. A movement's order is the `humans_ticket_order_id` in the connector's payment metadata (movement metadata overrides intent metadata). Reporting categories map through `PaymentLedgerEntry.KindOf`, shared with pretix and Hi.Events. The connector owns period selection, older PaymentIntent reads and automatic payout contents.

## Settings

| Variable | Purpose |
|---|---|
| `STRIPE_TICKETS_KEY` | Shared restricted key on the Tickets Stripe account: Checkout Sessions write, PaymentIntents, Charges and Disputes read, Refunds write. Reporting reads use the `Tickets` entry in `Stripe:MirrorAccounts`. Unset or whitespace-containing means `IsConfigured` is false and Tickets takes no orders on this platform |
| `STRIPE_BOXOFFICE_WEBHOOK_SECRET` | Signing secret of the endpoint `https://<host>/BoxOffice/StripeWebhook` subscribed to `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `checkout.session.expired`, `charge.refunded`, `refund.updated`, `refund.failed`, `charge.dispute.created`, `charge.dispute.closed`, `charge.dispute.funds_withdrawn`, `charge.dispute.funds_reinstated`. Without it, Tickets still learns outcomes from the return page and its 5-minute poll |

Tickets' `/Tickets/Admin/Sales` shows whether the platform is configured.

## Negative Access Rules

- The webhook never trusts its payload for state: Tickets re-reads the session from Stripe.
- No page here; buyers and admins act only through Tickets' pages and policies.
- No other section **can** name `BoxOfficePlatform` or `BoxOfficeSettings`: both are internal.
- `ErasePersonalDataAsync` **cannot** remove anything: Stripe offers no way to strip a session, and this section holds
  nothing.

## Triggers

- A verified webhook event calls `ITicketingPlatformEvents.PlatformOrderChangedAsync("boxoffice", sessionId)` for each session
  it names.

## Cross-Section Dependencies

- **Tickets**: implements `ITicketingPlatform`, calls `ITicketingPlatformEvents`, both from the `Humans.Tickets.Contracts` leaf.
- **Base**: configuration registry.
- **Stripe**: `IStripeService` reads the `Tickets` account ledger and fees and runs connection checks. Checkout sessions, refunds, disputes and webhook verification remain in this adapter, using the shared Tickets key.

## Architecture

**Owning services:** `BoxOfficePlatform`
**Owned tables:** None, adapter over Tickets' port.
**Status:** (A) Migrated, the adapter shape: no tables, no public contract, one webhook controller (built 2026-11-21).

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| Read contract | — | None: Tickets resolves `ITicketingPlatform` by key `boxoffice` |
| Write contract | — | None |

### For (A) Migrated sections

- No repository and no DbContext: the service reads and writes Stripe through Stripe.NET only.
- No caching decorator: every call exists to see Stripe's current state.
- Cross-section calls: `ITicketingPlatformEvents` from the webhook.
- Architecture enforcement: the anonymous webhook is allow-listed in
  `tests/Humans.Web.Tests/Authorization/EndpointAuthorizationTests.cs`; the project references Base, Stripe and
  `Humans.Tickets.Contracts`.
