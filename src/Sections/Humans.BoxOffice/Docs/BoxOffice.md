<!-- freshness:triggers
  src/Sections/Humans.BoxOffice/**
  tests/Humans.BoxOffice.Tests/**
-->

# BoxOffice — Section Invariants

Humans' own ticket checkout: one implementation of Tickets' `ITicketCheckoutBackend` port, key `boxoffice`, that
takes payment through Stripe hosted Checkout and refunds through the Stripe API. It owns no tables and no Tickets data:
Tickets owns the order, the price and the named ticket; this section only turns them into a Stripe Checkout Session
and reads it back. Spec: `src/Sections/Humans.Tickets/Docs/features/checkout.md`.

## Concepts

- The backend order id is the Checkout Session id. Each order line becomes one Stripe line at exactly its amount
  (zero lines are left out); the order id is the idempotency key, the `client_reference_id` and the
  `humans_ticket_order_id` metadata on the session and its PaymentIntent.
- Payment methods are whatever the Stripe account enables for Checkout (card, Klarna, iDEAL). The locale follows the
  buyer's culture where Stripe has it; Catalan falls back to Stripe's choice.
- Status is read from the session with its PaymentIntent, latest charge and balance transaction: `complete` + `paid` is
  Paid, `expired` is Expired, a completed session whose async payment was cancelled is Failed, everything else Pending.
  Fee is the balance transaction's total fee; refunded total is the charge's `amount_refunded`, so dashboard refunds are
  seen too.

## Invariants

- Charges EUR only; a non-EUR request, a negative line or a non-positive total throws `TicketCheckoutException` before
  any Stripe call. The adapter never changes an amount.
- Session expiry is clamped to Stripe's 30 minutes to 24 hours.
- A refund goes only against the session's own PaymentIntent, never above what the charge still holds (amount minus
  `amount_refunded`), with the Humans refund id as the idempotency key. Tickets' own cap (ticket lines net of earlier
  refunds) sits in front. This is the one money-out exception in `memory/architecture/stripe-money-out.md`.
- The webhook (`POST /BoxOffice/StripeWebhook`, anonymous, Stripe-signature verified, 503 while unset) only names
  sessions to `ITicketCheckoutEvents`: `checkout.session.*` events by their session, `charge.refunded` by the sessions
  of the refunded PaymentIntent. An invalid signature does nothing.

## Settings

| Variable | Purpose |
|---|---|
| `STRIPE_BOXOFFICE_KEY` | Restricted key on the ticket Stripe account: Checkout Sessions write, PaymentIntents, Charges and Balance transactions read, Refunds write. Unset means `IsConfigured` is false and Tickets opens no checkout on this backend |
| `STRIPE_BOXOFFICE_WEBHOOK_SECRET` | Signing secret of the endpoint `https://<host>/BoxOffice/StripeWebhook` subscribed to `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `checkout.session.async_payment_failed`, `checkout.session.expired`, `charge.refunded`. Without it, Tickets still learns outcomes from the return page and its 5-minute poll |

Tickets' `/Tickets/Admin/Sales` shows whether the backend is configured.

## Negative Access Rules

- The webhook never trusts its payload for state: Tickets re-reads the session from Stripe.
- No page here; buyers and admins act only through Tickets' pages and policies.
