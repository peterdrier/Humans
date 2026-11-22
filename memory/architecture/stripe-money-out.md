---
name: Stripe money-out — never through the API; refunds are issued on stripe.com and read back
description: "HARD RULE. Humans never calls a Stripe API that moves money out (payouts, transfers, chargebacks; Store refunds are done on stripe.com). One exception: ticket checkout backends may refund a specific ticket payment, capped at what its tickets cost."
---

Money that leaves a Stripe account cannot be taken back. Refunds, payouts and chargeback responses are done by a person on stripe.com, except the ticket refunds below; Humans never calls those APIs (Peter, 2026-11-19, nobodies-collective/Humans#1254, replacing the 2026-10-27 guarded API refund path).

**Why:** A bug, or a compromised deploy, that can move money out can drain the account. Keeping every Stripe key Humans holds unable to move money removes that risk; the click on stripe.com is cheap.

**How to apply:**
- No key Humans holds gets `refund:write`, payout or transfer scopes. `IStripeService` gains no money-out method; push back on any refund, payout, transfer or charge-modify call.
- For a camp order owed money, the order page links each paid Stripe payment to its dashboard page (`IStripeService.StorePaymentDashboardUrl`). A FinanceAdmin/Admin refunds there, then presses "Check for refunds": Humans reads the payment's refunds (`ListStoreRefundsAsync`, the org read key) and books each one not yet booked as a negative `PaymentMethod.Refund` row citing the Stripe refund id, with an audit entry. A refund Stripe is still sending is booked `Pending` and already lowers the amount owed, so it can't be sent twice; later checks move it to `Paid`, or to `Failed` if Stripe reports it failed. Running it again books nothing new.
- A refund made outside Stripe can still be booked by hand on `/Store/Order/{id}/RecordPayment`, citing its reference. Book a Stripe refund by hand only with its `re_` id, or the check will book it a second time.
- The Stripe webhook never inserts refund rows; it categorizes the four `checkout.session.*` events only.

**The one exception: ticket refunds (Peter, 2026-11-21).** A ticket checkout backend (Box Office, behind Tickets' `ITicketCheckoutBackend.RefundAsync`) may refund through the API, because the limit is tied to one refund of one past payment, which is not the same as being able to send money anywhere. Both guards are required and tested:
- Tickets refunds only a paid order, and never more than its ticket lines cost net of every earlier refund that did not fail; donations are never refunded (`CheckoutOrderDetail.RefundableMinor`).
- The adapter refunds only against that order's own payment, never above what the payment still holds, with the Humans refund id as the idempotency key.
- Box Office has its own restricted key (`STRIPE_BOXOFFICE_KEY`, Refunds write); no other key Humans holds gains a money-out scope. Payouts, transfers and charge edits stay forbidden everywhere.

**Related:** `code/stripe-restricted-keys.md`, `src/Sections/Humans.Store/Docs/Store.md` "Stripe Connector".
