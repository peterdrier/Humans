<!-- freshness:triggers
  src/Sections/Humans.BoxOffice/**
  src/Sections/Humans.Tickets.Contracts/ITicketingPlatform.cs
  tests/Humans.BoxOffice.Tests/**
-->

# BoxOffice — Target Shape

The shape this section is converging on, regenerated every section-doctor run and diffed
against the previous run's copy. Not a history: what the section *should* be today.

## 1. What the section does

It is the way to sell a ticket with no outside ticketing platform at all. When a member buys, Humans
already knows the order, the price and whose name goes on each ticket; this section turns that
into a Stripe payment page on the association's ticket Stripe account, and later tells Humans
what became of it: paid, still settling, failed, expired, refunded or charged back. When the ticketing
team gives money back, it refunds through Stripe, at most once per refund and never more than
the payment still holds. For the books it reads the ticket account back: every payment page
opened in a period, every balance movement with the order it belongs to, and every payout to the
bank with what it carried. When Stripe calls back, it checks the call is genuine and only tells
Humans which payment to look at again. It stores nothing of its own.

## 2. The shapes

| Shape | Members | What the caller is asking |
|---|---|---|
| **Take a payment** | `CreateOrderAsync` | "Open a payment page that charges exactly these lines." |
| **What became of it?** | `GetStatusAsync` | "Read this payment back from Stripe: state, fee, method, refunded, charged back." |
| **Give money back** | `RefundAsync` | "Refund this exact amount, once, against this payment." |
| **Stop taking money** | `CancelAsync` | "Close this unpaid page; tell me if it is too late." |
| **Reconcile** | `ListOrdersAsync`, `GetPaymentLedgerAsync`, `EventRefOf` | "What does the ticket account hold for this period, order by order and movement by movement?" |
| **Is it wired up?** | `IsConfigured`, `Setup`, `CheckAsync` | "Can this platform sell, and what would an admin fix?" Read-only. |
| **Nudge** | `ParseWebhookAsync` behind `POST /BoxOffice/StripeWebhook` | "Stripe says something changed: which payments?" Names sessions, decides nothing. |
| **Forget a person** | `ErasePersonalDataAsync` | "Remove this buyer." A no-op: Stripe keeps its records as processor and this section holds none. |

Every read except the probe throws `TicketingPlatformException` rather than answer partly; the
probe answers findings and never throws.

## 3. Structure

```
Section.cs                               env-var binding, DI, settings inventory, admin sidebar row
Services/BoxOfficePlatform.cs     the port's only implementation + BoxOfficeSettings
Controllers/BoxOfficeStripeWebhookController.cs   the anonymous, signature-checked nudge
Docs/                                    BoxOffice.md (invariants), data-access.md, health.md (this)
```

- One adapter class per port, the same shape as the pretix and Hi.Events adapters. Its shapes
  share one Stripe client and one session read, so a split would add types and seams without
  removing a concept.
- `BoxOfficeSettings` stays beside the adapter: internal, bound in `Section.cs` and read only by the adapter.

## 4. Invariants

- **Exact EUR price, before any Stripe call.** A non-EUR request or a negative line or a
  non-positive total throws (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:63`,
  `src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:65`); each positive line
  becomes one Stripe line at exactly its amount, free lines are left out
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:90`).
- **One payment page per order.** The Stripe idempotency key is built from the order id
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:114`).
- **Session lifetime stays inside Stripe's bounds**, clamped to 31 minutes .. 23 hours from now
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:43`,
  `src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:70`).
- **State is derived from the session and its PaymentIntent**, never stored: complete and paid
  is Paid, expired is Expired, complete with a cancelled or reset PaymentIntent is Failed, other
  complete is Processing, anything else Pending
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:139`).
- **Refunded means money that went back**: only `succeeded` refunds count, and only a failed
  refund carrying a Humans refund id sets `RefundFailed`
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:161`,
  `src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:162`).
- **A chargeback counts while its Stripe dispute holds money**: not won, not a `warning_*` inquiry
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:328`).
- **A refund never repeats and never exceeds the payment.** It needs a paid charge
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:358`); a refund already
  carrying the Humans refund id answers its own state
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:364`); the amount is
  capped at what the charge still holds
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:368`); the idempotency key is
  built from the refund id (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:386`).
  A Stripe refusal answers Failed; no answer throws so Tickets repeats it
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:391`,
  `src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:396`).
- **Cancel refuses only a payment that is or may still be paid**
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:413`).
- **The webhook trusts nothing but the signature.** 503 while either variable is unset
  (`src/Sections/Humans.BoxOffice/Controllers/BoxOfficeStripeWebhookController.cs:28`), 400 on a
  bad signature (`src/Sections/Humans.BoxOffice/Controllers/BoxOfficeStripeWebhookController.cs:37`),
  and a verified event yields only session ids
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:507`).
- **Secrets are shown only as set or missing**
  (`src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:431`,
  `src/Sections/Humans.BoxOffice/Services/BoxOfficePlatform.cs:433`).
- **No tables, no Store Stripe.** The project references only Base and the Tickets contracts leaf
  (`src/Sections/Humans.BoxOffice/Humans.BoxOffice.csproj:21`,
  `src/Sections/Humans.BoxOffice/Humans.BoxOffice.csproj:22`).

## 5. Seams

none

## 6. Deliberately not done

- **No public contract.** Tickets resolves the port by key; nothing else names this section.
- **No split of the adapter by shape** (see §3).
- **No Stripe products, prices, coupons or invoices**, and no tax lines: Stripe charges the
  amounts Humans priced.
- **No erasure inside Stripe.** Stripe keeps payment records as processor.
- **No listing of payment methods.** Card, iDEAL and Klarna are dashboard switches.
- **No settlement date for a delayed debit.** `PaidAt` is when the buyer completed the page; the order reads Processing until the money settles (Peter, 2026-11-22).

## Load-bearing weirdness

- **API refunds here are the one money-out exception** to `memory/architecture/stripe-money-out.md`,
  doubly capped: Tickets' own cap in front, the charge's remainder behind.
- **`ListOrdersAsync` lists every session on the account**, not only Humans' own: the ticket
  account sells tickets only, so a session Humans did not open is exactly what reconciliation
  must show.
- **The list reads refunds only where the charge shows some.** That misses a failed refund (it
  drops out of the charge's total), which is fine for totals and why `GetStatusAsync` reads every
  refund.
- **The ledger ties a movement to its order through the PaymentIntent** when the charge, refund
  or dispute carries no order id of its own; PaymentIntents from a day before the period are
  listed once, older ones read one by one.
- **A payout's contents are read only when Stripe finished reconciling an automatic payout**;
  before that a partial list would read as a mismatch.
- **Its own key and webhook**, apart from the Stripe section's Store integration, on a Stripe
  account kept for tickets.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-11-21 | First doctor pass: the refund, cancel and checkout guards and the whole webhook path pinned by tests; BoxOffice.md and the comments brought back to the code | peterdrier/Humans#2558 |
