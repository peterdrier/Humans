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

**Deliberate choices.** Its own restricted key and webhook on the ticket
Stripe account, apart from the Store's Stripe integration. API refunds are allowed here as the one money-out exception.
This section stores nothing: every order, refund, ticket and timeline entry lives in Tickets' tables
(`ticket_checkout_orders`, `ticket_checkout_refunds`, `ticket_checkout_tickets`, `ticket_checkout_order_events`), the
same for every platform.

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
  Fee is the balance transaction's total fee, read through `IStripeService.GetAccountPaymentFeeAsync` on the `BoxOffice` account; unknown fees stay null and never block a paid order; refunded total is the sum of the PaymentIntent's `succeeded` refunds (Stripe's `amount_refunded` also counts pending ones), so dashboard refunds are
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

- Charges EUR only; a non-EUR request, a negative line or a non-positive total throws `TicketingPlatformException` before
  any Stripe call. The adapter never changes an amount.
- Session expiry is clamped to 31 minutes .. 23 hours from now, inside Stripe's 30 minutes .. 24 hours.
- A refund goes only against the session's own PaymentIntent, never above what the charge still holds (amount minus
  `amount_refunded`), with the idempotency key `humans_ticket_refund_<refund id>`. Tickets' own cap (ticket lines net of earlier
  refunds) sits in front. This is the one money-out exception in `memory/architecture/stripe-money-out.md`.
- The webhook (`POST /BoxOffice/StripeWebhook`, anonymous, Stripe-signature verified, 503 while either variable is
  unset) only names sessions to `ITicketingPlatformEvents`, by the event's object: a Checkout Session names itself; a
  charge, refund or dispute names the sessions of its PaymentIntent; anything else names none. An invalid signature
  answers 400, so a wrong secret shows as failed deliveries in Stripe.
- Cancelling expires the session; Stripe refusing that is an error only when the session is complete and not failed.
- `Setup` shows variables only as set or missing, and test or live mode from the checkout key prefix. `CheckAsync` uses `IStripeService.CheckAccountAsync` on the named `BoxOffice` account for balance transaction, PaymentIntent and payout read probes, plus a Checkout Session probe using the actual checkout key, each its own finding, and reports a missing webhook secret; it cannot test write permissions without a write.
- Backend setup checks propagate caller cancellation instead of reporting a vendor failure; independent transport timeouts remain diagnostic findings.
- `ListOrdersAsync` lists every Checkout Session created in the period, whatever made it, since the account sells tickets only. Each session carries `humans_ticket_count` (ticket lines, free ones included) for reconciliation; one without it has no count. Refunds are read only for a charge that shows some. `EventRefOf` is always null.
- `GetPaymentLedgerAsync` reads balance transactions and payouts through `IStripeService.GetAccountLedgerAsync` on the named `BoxOffice` account. Unconfigured reporting returns null; Stripe refusals throw `TicketingPlatformException`. The connector reads `humans_ticket_order_id` from expanded charge/refund/dispute metadata first, otherwise from the PaymentIntent. A valid source id avoids retrieving an older PaymentIntent. `PaymentLedgerEntry.KindOf` supplies the shared reporting-category classification for Box Office, pretix and Hi.Events. Payout contents retain the connector's settled-automatic-only rule; manual or still-reconciling payouts have no contents total.

## Settings

| Variable | Purpose |
|---|---|
| `STRIPE_BOXOFFICE_KEY` | Restricted key on the ticket Stripe account: Checkout Sessions write, PaymentIntents, Charges and Disputes read, Refunds write. Unset means `IsConfigured` is false and Tickets takes no orders on this platform |
| `STRIPE_BOXOFFICE_WEBHOOK_SECRET` | Signing secret of the endpoint `https://<host>/BoxOffice/StripeWebhook` subscribed to `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `checkout.session.expired`, `charge.refunded`, `refund.updated`, `refund.failed`, `charge.dispute.created`, `charge.dispute.closed`, `charge.dispute.funds_withdrawn`, `charge.dispute.funds_reinstated`. Without it, Tickets still learns outcomes from the return page and its 5-minute poll |

Fees, payouts and read probes require a `Stripe:MirrorAccounts` entry named `BoxOffice`, with this same account's id and a key with Balance transactions, Payouts, PaymentIntents, Charges, Refunds and Disputes read. The Checkout Session probe uses the actual checkout key separately. `KeyVariable` must select a key for the same live or test account used by checkout. The checkout key can also serve as that entry's own key when it has those read scopes. Missing configuration is shown by the platform connection test.

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
- **Stripe**: `IStripeService` owns the named account reporting reads (ledger, fees and read probes). Existing payment-lifecycle calls still use Box Office's restricted key and SDK client.

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

- No repository and no DbContext. Account reporting reads use `IStripeService`; payment-lifecycle calls retain the existing Stripe.NET client.
- No caching decorator: every call exists to see Stripe's current state.
- Cross-section calls: `ITicketingPlatformEvents` from the webhook; `IStripeService` for account reporting.
- Architecture enforcement: the anonymous webhook is allow-listed in
  `tests/Humans.Web.Tests/Authorization/EndpointAuthorizationTests.cs`; the project references Base,
  `Humans.Tickets.Contracts` and `Humans.Stripe`.
