---
name: Stripe money-out — refunds only through the guarded API path; payouts and chargebacks stay dashboard-manual
description: HARD RULE. Payouts and chargebacks are never called from Humans. Refunds may be, but only through the one guarded refund path (nobodies-collective/Humans#1254); until it ships, refunds are issued on stripe.com and booked by hand.
---

Money that leaves a Stripe account cannot be taken back. Payouts and chargeback responses are always done by a person on stripe.com; Humans never calls those APIs. Refunds may go through the API, but only through one guarded path (nobodies-collective/Humans#1254, decided by Peter 2026-10-27).

**Why:** A bug, or a compromised deploy, that can move money out can drain the account. Refunds are routine enough to be worth automating the click, but only behind limits that make a wrong or repeated refund impossible.

**How to apply:**
- The refund path, and only it, may move money out. It refunds a payment already recorded on the order, never more than that payment minus refunds already booked against it; a FinanceAdmin/Admin starts it by hand, never a job or webhook; it sends a Stripe idempotency key; it books the negative `Refund` payment and an audit entry in the same action.
- `IStripeService` gains exactly one money-out method, the refund. Push back on any payout, transfer or charge-modify method.
- The Store key may hold `refund:write` once the path ships; no key ever holds payout or transfer scopes.
- Until #1254 ships, a refund is issued on stripe.com and booked as a negative `PaymentMethod.Refund` row on `/Store/Order/{id}/RecordPayment`, citing the Stripe refund id. The Stripe webhook never inserts refund rows; it categorizes the four `checkout.session.*` events only.

**Related:** `code/stripe-restricted-keys.md`, `src/Sections/Humans.Store/Docs/Store.md` "Stripe Connector".
