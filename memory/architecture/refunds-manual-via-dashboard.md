---
name: Refunds, payouts, and chargebacks are Stripe-dashboard manual — never via API from Humans
description: HARD RULE. Humans never calls Stripe refund/payout APIs. Money-out is human-initiated on stripe.com. The app's role is bookkeeping (negative `StorePayment` rows when a refund occurs), not refund execution.
---

Humans never invokes Stripe's refund, payout, or charge-modify APIs. Any operation that moves money OUT of a Stripe account is performed manually by a finance admin on stripe.com.

**Why:** Two reasons compounding:
1. **Blast radius.** A bug — or a compromised production deploy — that can call refund APIs can drain the account programmatically. Keeping money-out gated behind a deliberate human click on stripe.com means an attacker would need Stripe Dashboard credentials too.
2. **Audit clarity.** Stripe's own audit trail for human-initiated refunds is far cleaner than what Humans would synthesize. The dashboard records who clicked what, when, with what reason. Reproducing that fidelity in our audit log to match would be ceremony for no incremental safety.

**How to apply:** Production Stripe keys (RAKs — see `code/stripe-restricted-keys.md`) must NOT have `refund:write`, `payout:write`, `charge:write`, or any other money-out scopes. The `StripeService` connector must not expose refund/payout methods on `IStripeService` — if a future PR proposes one, push back and route the operation through the dashboard instead. Refund **bookkeeping** within Humans posts a negative `StorePayment` row via FinanceAdmin manual entry (Phase 5.3) — `Method = StorePaymentMethod.Manual`, `AmountEur < 0`, `Notes` cites the Stripe refund id. The Stripe webhook never inserts refund rows; it only handles `checkout.session.completed`.

**Related:** `code/stripe-restricted-keys.md`, `docs/sections/Store.md` "Stripe Configuration".
