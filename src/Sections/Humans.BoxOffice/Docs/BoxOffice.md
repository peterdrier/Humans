<!-- freshness:triggers
  src/Sections/Humans.BoxOffice/**
  src/Sections/Humans.Tickets.Contracts/ITicketCheckoutBackend.cs
  tests/Humans.BoxOffice.Tests/**
-->
<!-- freshness:flag-on-change
  The session status mapping, refund idempotency and cap, and the webhook's event list and trust model.
-->

# BoxOffice — Section Invariants

Humans' own ticket checkout: one implementation of Tickets' `ITicketCheckoutBackend` port, key `boxoffice`, that
takes payment through Stripe hosted Checkout and refunds through the Stripe API. It owns no tables and no Tickets data:
Tickets owns the order, the price and the named ticket; this section only turns them into a Stripe Checkout Session
and reads it back. Spec: `src/Sections/Humans.Tickets/Docs/features/checkout.md`.

## Purpose

Box Office is the option with no ticketing vendor at all: Humans sells, and Stripe only takes the card, Klarna or
iDEAL payment on its hosted page and refunds it. It needs no container to run, only a Stripe account kept for tickets.

**What good looks like.** Every Checkout Session matches one Humans order to the cent, every state Stripe can put a
payment in (3-D Secure, a debit settling for days, a failed debit, a chargeback, a refund made in the dashboard)
reaches Tickets, and a repeated call never charges or refunds twice. What Stripe charged, refunded, kept as fees
and paid out to the bank reconciles against Humans' orders per period, with a drill-down where the two differ.

**Deliberate choices.** Its own restricted key and webhook on the ticket
Stripe account, apart from the Store's Stripe integration. API refunds are allowed here as the one money-out exception.
This section stores nothing: every order, refund, ticket and timeline entry lives in Tickets' tables
(`ticket_checkout_orders`, `ticket_checkout_refunds`, `ticket_checkout_tickets`, `ticket_checkout_order_events`), the
same for all three backends.

**Out of scope.** Stripe products, prices, coupons and invoices; erasing data inside
Stripe, which keeps it as the payment processor.

## Concepts

- The backend order id is the Checkout Session id. Each order line becomes one Stripe line at exactly its amount
  (zero lines are left out); the order id is the idempotency key, the `client_reference_id` and the
  `humans_ticket_order_id` metadata on the session and its PaymentIntent.
- Payment methods are whatever the Stripe account enables for Checkout (card, Klarna, iDEAL). The locale follows the
  buyer's culture where Stripe has it; Catalan falls back to Stripe's choice.
- Status is read from the session with its PaymentIntent, latest charge and balance transaction: `complete` + `paid` is
  Paid, `expired` is Expired, a completed session whose PaymentIntent is `canceled` or back at `requires_payment_method`
  (an async debit that failed) is Failed, any other completed session is Processing (a SEPA debit or bank transfer
  still settling, which Stripe no longer lets anyone expire), and an open session is Pending. 3-D Secure and other
  buyer actions happen on Stripe's page while the session is open. A disputed charge reports the amount of its
  disputes that hold money (not `won`, not a `warning_*` inquiry) as `DisputedMinor`.
  Fee is the balance transaction's total fee; refunded total is the sum of the PaymentIntent's `succeeded` refunds (Stripe's `amount_refunded` also counts pending ones), so dashboard refunds are
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

- Charges EUR only; a non-EUR request, a negative line or a non-positive total throws `TicketCheckoutException` before
  any Stripe call. The adapter never changes an amount.
- Session expiry is clamped to Stripe's 30 minutes to 24 hours.
- A refund goes only against the session's own PaymentIntent, never above what the charge still holds (amount minus
  `amount_refunded`), with the Humans refund id as the idempotency key. Tickets' own cap (ticket lines net of earlier
  refunds) sits in front. This is the one money-out exception in `memory/architecture/stripe-money-out.md`.
- The webhook (`POST /BoxOffice/StripeWebhook`, anonymous, Stripe-signature verified, 503 while unset) only names
  sessions to `ITicketCheckoutEvents`: `checkout.session.*` events by their session, `charge.refunded` by the sessions
  of the refunded PaymentIntent. An invalid signature answers 400, so a wrong secret shows as failed deliveries in Stripe. `refund.*` and `charge.dispute.*` events are mapped like `charge.refunded`.
- Cancelling expires the session; Stripe refusing that is an error only when the session is complete and not failed.
- `Setup` shows both variables only as set or missing, and test or live mode from the key prefix. `CheckAsync` lists one Checkout Session and reports a missing webhook secret; it cannot test the refund permission without refunding.

## Settings

| Variable | Purpose |
|---|---|
| `STRIPE_BOXOFFICE_KEY` | Restricted key on the ticket Stripe account: Checkout Sessions write, PaymentIntents, Charges, Disputes and Balance transactions read, Refunds write. Unset means `IsConfigured` is false and Tickets opens no checkout on this backend |
| `STRIPE_BOXOFFICE_WEBHOOK_SECRET` | Signing secret of the endpoint `https://<host>/BoxOffice/StripeWebhook` subscribed to `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `checkout.session.expired`, `charge.refunded`, `refund.updated`, `refund.failed`, `charge.dispute.created`, `charge.dispute.closed`, `charge.dispute.funds_withdrawn`, `charge.dispute.funds_reinstated`. Without it, Tickets still learns outcomes from the return page and its 5-minute poll |

Tickets' `/Tickets/Admin/Sales` shows whether the backend is configured.

## Negative Access Rules

- The webhook never trusts its payload for state: Tickets re-reads the session from Stripe.
- No page here; buyers and admins act only through Tickets' pages and policies.
- Code outside this project **cannot** name `BoxOfficeCheckoutBackend` or `BoxOfficeSettings`.
- `ErasePersonalDataAsync` **cannot** remove anything: Stripe offers no way to strip a session, and this section holds
  nothing.

## Triggers

- A verified webhook event calls `ITicketCheckoutEvents.CheckoutChangedAsync("boxoffice", sessionId)` for each session
  it names.

## Cross-Section Dependencies

- **Tickets**: implements `ITicketCheckoutBackend`, calls `ITicketCheckoutEvents`, both from the `Humans.Tickets.Contracts` leaf.
- **Base**: configuration registry.
- **Stripe** (section): none. Box Office uses its own key and Stripe.NET client, not `IStripeService`.

## Architecture

**Owning services:** `BoxOfficeCheckoutBackend`
**Owned tables:** None, adapter over Tickets' port.
**Status:** (A) Migrated, the adapter shape: no tables, no public contract, one webhook controller.
