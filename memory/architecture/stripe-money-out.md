---
name: Stripe money-out — never through the API; refunds are issued on stripe.com and read back
description: "HARD RULE. Humans never calls a Stripe API that moves money out: refunds, payouts and chargebacks are done by a person on stripe.com. Humans links to the payment and books refunds it reads back."
---

Money that leaves a Stripe account cannot be taken back. Refunds, payouts and chargeback responses are always done by a person on stripe.com; Humans never calls those APIs (Peter, 2026-11-19, nobodies-collective/Humans#1254, replacing the 2026-10-27 guarded API refund path).

**Why:** A bug, or a compromised deploy, that can move money out can drain the account. Keeping every Stripe key Humans holds unable to move money removes that risk; the click on stripe.com is cheap.

**How to apply:**
- No key Humans holds gets `refund:write`, payout or transfer scopes. `IStripeService` gains no money-out method; push back on any refund, payout, transfer or charge-modify call.
- For a camp order owed money, the order page links each paid Stripe payment to its dashboard page (`IStripeService.StorePaymentDashboardUrl`). A FinanceAdmin/Admin refunds there, then presses "Check for refunds": Humans reads the payment's refunds (`ListStoreRefundsAsync`, the org read key) and books each succeeded one not yet booked as a negative `PaymentMethod.Refund` row citing the Stripe refund id, with an audit entry. Running it again books nothing new.
- A refund made outside Stripe can still be booked by hand on `/Store/Order/{id}/RecordPayment`, citing its reference. Book a Stripe refund by hand only with its `re_` id, or the check will book it a second time.
- The Stripe webhook never inserts refund rows; it categorizes the four `checkout.session.*` events only.

**Related:** `code/stripe-restricted-keys.md`, `src/Sections/Humans.Store/Docs/Store.md` "Stripe Connector".
