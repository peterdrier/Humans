<!-- freshness:triggers
  src/Sections/Humans.Pretix/**
  src/Sections/Humans.Tickets.Contracts/ITicketCheckoutBackend.cs
  tests/Humans.Pretix.Tests/**
  deploy/pretix/**
-->

# Pretix — Target Shape

The shape this section is converging on, regenerated every section-doctor run and diffed
against the previous run's copy. Not a history: what the section *should* be today.

## 1. What the section does

It lets Humans use a stock pretix instance as its payment desk. When a buyer checks out on an
event sold through pretix, Humans' order becomes a pretix order at exactly Humans' prices, and
the buyer is sent to pretix's pay page and then back to Humans. Afterwards it answers what
pretix holds for that order: paid or not, how it was paid, what went back as a refund or a
chargeback. It refunds an exact amount through pretix, closes an unpaid order when Humans' hold
lapses, and clears the buyer's contact details from the pretix order on erasure. For the
ticketing team it describes its settings, checks that an event is ready to sell, and lists an
event's orders for reconciliation. pretix tells it when an order changes; it passes that on to
Tickets, which reads the order again. Nothing about the catalogue, capacity or buyer mail lives
in pretix.

## 2. The shapes

| Shape | Members | What the caller is asking |
|---|---|---|
| **Take a payment** | `PretixCheckoutBackend.CreateCheckoutAsync` | "Charge exactly these lines and give me the pay link." |
| **What does pretix hold?** | `GetStatusAsync`, `MapStatus` | "Is this order paid, how, and what went back?" |
| **Give money back** | `RefundAsync` | "Refund this amount once, and tell me how it ended." |
| **Close the hold** | `CancelAsync` | "Stop taking money on this unpaid order." |
| **Forget the buyer** | `ErasePersonalDataAsync` | "Remove this email from the order." |
| **Connect and check** | `Setup`, `CheckAsync`, `PretixSettings`, `Section` (settings and admin row) | "What must be set, and is this event ready to sell?" |
| **Reconcile** | `ListOrdersAsync`, `EventRefOf`, `GetPaymentLedgerAsync` | "Every order pretix holds for this event and period." |
| **Hear a change** | `PretixWebhookController.Receive` | "pretix says an order changed." |

## 3. Structure

```
Section.cs                                 settings binding, typed HttpClient, port registration, admin row
Services/PretixSettings.cs                 one organizer: instance root, slug, token, webhook secret
Services/PretixCheckoutBackend.cs          the port against pretix's REST API, and its wire records
Controllers/PretixWebhookController.cs     the nudge endpoint
Contracts/README.md                        deliberately empty
Docs/                                      Pretix.md (invariants), data-access.md, debt.yml, health.md (this)
```

- One adapter class for all port calls. Every money question (what the buyer paid, what went
  back, whether the order is covered) is answered from the same payment and refund sums, so
  status, refund safety and reconciliation cannot disagree.
- Wire records mirror only the pretix fields the adapter reads.

## 4. Invariants

- The order code derives from the Humans order id, so a retried create finds its own order:
  `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:80`, and a code another Humans
  order holds throws: `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:96`; pinned
  by `Create_RetriedWithSameOrderId_ReturnsTheExistingOrder` and `Create_CodeOwnedByAnotherOrder_Throws`.
- pretix charges exactly the lines or no order survives: a price, tax or total mismatch cancels a
  just-created order and throws, `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:115`;
  pinned by `Create_TaxedDifferentlyThanAsked_CancelsTheOrderAndThrows` and
  `Create_ChargedDifferentlyThanAsked_CancelsTheOrderAndThrows`.
- Money decides the status: a pending or expired order its payments cover, or a cancelled order
  holding unrefunded money, reads Paid, `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:454`;
  pinned by `Status_OrderItsPaymentsCover_IsPaid` and `Status_CancelledOrderHoldingMoney_IsPaid`.
- A chargeback is disputed, never refunded: `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:471`;
  pinned by `Status_Chargeback_IsDisputedNotRefundedUntilCancelledInPretix`.
- A refund id refunds once: a repeat answers the earlier refund,
  `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:153`, and a retry reuses the
  marked fee, `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:171`; pinned by
  `Refund_RepeatedRefundId_DoesNotRefundTwice` and `Refund_RetryAfterTheFeeWasAdded_DoesNotLowerTheTotalTwice`.
- A refund pretix accepted but has not finished never reads Succeeded:
  `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:619`; pinned by
  `Refund_AcceptedButNotYetDone_IsNotReportedSucceeded`.
- No answer from pretix on a refund throws, so Tickets retries:
  `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:183`; pinned by
  `Refund_PretixServerError_ThrowsAndTheRetryReusesTheFee`.
- Cancel refuses a paid or settling order: `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:225`;
  pinned by `Cancel_PaidOrder_Throws` and `Status_PendingOrderWithASettlingPayment_IsProcessingAndCannotBeCancelled`.
- No names go to pretix: the order body carries email, code, comment and amounts only,
  `src/Sections/Humans.Pretix/Services/PretixCheckoutBackend.cs:544`; pinned by the last
  assertion of `Create_PostsHumansPricesAndReturnsPayLinkBackToHumans`.
- The webhook changes nothing itself: a wrong secret is 404,
  `src/Sections/Humans.Pretix/Controllers/PretixWebhookController.cs:40`, an unset one 503,
  `src/Sections/Humans.Pretix/Controllers/PretixWebhookController.cs:32`, and a match only
  nudges Tickets, `src/Sections/Humans.Pretix/Controllers/PretixWebhookController.cs:47`;
  pinned by `PretixWebhookControllerTests`.

## 5. Seams

- **Per-order data shred.** Erasure clears the order's contact fields, but pretix keeps the old
  email in the order log and the Stripe payment details on the payment until its event-wide
  shredder runs; a per-order shred in the pretix fork is the open fix (debt PRETIX-1).

## 6. Deliberately not done

- **No mirror of Humans' catalogue in pretix.** Two free-price carrier items per event carry
  every price; waves, classes and capacity stay in Humans.
- **No Humans settings per pretix event.** The event slug comes per order from Tickets.
- **No trust in the webhook body.** Tickets re-reads the order; the body names it, nothing more.
- **No `Contracts/` surface.** The port belongs to Tickets.

## Load-bearing weirdness

- **A refund first adds a negative fee to the pretix order.** Without it pretix reopens a paid
  order and its pay page asks the buyer for the refunded money again. The fee's internal type is
  also the refund's idempotency marker.
- **A cancelled or expired order can read Paid.** pretix keeps a late confirmed payment but cannot
  mark the order paid (the carrier items have no quota); the port requires such a payment be reported.
- **Reconciliation keeps a paid order paid after a refund, while status nets it.** The list says
  what the order charged; the status read decides whether tickets issue.
- **`FeeMinor` is always null.** The port means what the processor kept, which pretix does not
  know; the buyer never pays a surcharge.
- **`DynamicProxyGenAssembly2` in `AssemblyInfo.cs`.** The repo-wide section convention, kept
  for test proxies of internal types.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-11-21 | First doctor pass: docs true about erasure and refund sums, money paths pinned | peterdrier/Humans#2557 |
