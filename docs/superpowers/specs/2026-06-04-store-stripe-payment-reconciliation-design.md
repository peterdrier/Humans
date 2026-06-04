# Store → Stripe Payment Reconciliation

**Date:** 2026-06-04
**Section:** Store
**Status:** Design approved (Peter), pre-implementation

## Problem

Store payments are recorded only by the Stripe webhook (`POST /Store/StripeWebhook` →
`HandleStripeCheckoutWebhookEventAsync` → `RecordStripePaymentAsync` → `StorePayment` row).
When `STRIPE_STORE_WEBHOOK_SECRET` is unset the endpoint returns 503 to every Stripe
delivery (`StoreStripeWebhookController.cs:24-28`), so a paid Checkout Session is never
recorded — the order balance stays at the full amount even though Stripe collected the
money. This happened in production: a paid €1800 camp order shows unpaid, and because the
webhook was misconfigured there is **no event left to redeliver**.

There is no path to recover such a payment today: `RecordManualPaymentAsync` throws
`NotSupportedException("Phase 5")`, and nothing polls Stripe. The webhook is the sole writer.

## Goal

A durable, always-available **reconciliation** between Stripe (source of truth for money
received) and our `StorePayment` records, surfaced as an admin screen under the Store
section. It is the operational home for Store-payment problems and management: it shows
webhook health, every Stripe payment matched to its order, what is unrecorded, and the
action to record the missing ones — idempotently, with all values pulled **from Stripe**,
never typed or guessed.

Reconciliation is mandatory infrastructure, not a one-off for the current €1800.

## Non-goals

- **Refunds / chargebacks / payouts** — stay 100% dashboard-manual per the Stripe-keys
  convention (`StripeSettings`). Out of scope.
- **Non-Stripe (cash / bank transfer) manual entry** — that is the separate Phase-5
  `RecordManualPaymentAsync` concern; not built here.
- **Automatic background sweep** — manual admin trigger only (re-runnable safely). A
  scheduled job is a trivial later add, deliberately deferred (YAGNI).
- **Amount-mismatch auto-correction** — mismatches are surfaced for humans, never auto-fixed.

## The join key

Every Checkout Session we create stamps `metadata["humans_store_order_id"]`
(`StripeService.cs:108-111`). That metadata is returned by the Stripe API even though the
dashboard's description column ("2026 - Yes") doesn't show it. So the **server** matches a
Stripe payment to its order reliably; a human never has to eyeball or paste a session id.

## Reconciliation status per Stripe payment

Computed in the Store service by diffing the Stripe session list against recorded
`StorePayment` rows (matched on PaymentIntent id):

| Status | Meaning | Action |
|---|---|---|
| **Recorded** | paid session, PI already in `store_payments` | none |
| **Missing** | `payment_status == paid`, order resolved, PI not recorded | recordable |
| **Unmatched** | paid but no/unknown/Team order in metadata | flagged for human; never auto-recorded |
| **Unpaid** | session `payment_status != paid` (open/expired/async-pending) | informational |

Reverse direction — **Orphan**: a recorded Stripe-method `StorePayment` whose PI id is
absent from the Stripe list — is reported read-only (never auto-deleted).

## Architecture

Layers respected: DbContext → Repository → Service → Controller. The Store service calls
its own repository and the `IStripeService` connector (the established Store pattern for
checkout + webhook parse). No cross-section access. Stripe SDK types stay behind the
connector seam (design-rules §15i).

### New surface (all flagged for Peter's sign-off)

**Domain**
- `AuditAction.StorePaymentsReconciled` — appended to the enum (positional; no migration).

**Application — connector (`IStripeService` / `StripeService`)**
- `Task<IReadOnlyList<StoreCheckoutSessionData>> ListStoreCheckoutSessionsAsync(CancellationToken ct = default)`
  — `SessionService.ListAutoPagingAsync` on the existing `StoreKey` (checkout_session Write ⊇ Read,
  no re-provisioning). Permission-error handling mirrors `CreateCheckoutSessionAsync`.
- **Reuse decision:** extend the existing `StoreCheckoutSessionData` record with
  `PaymentStatus` (string?) and `CreatedAt` (Instant?), populated in both the list path and
  the existing webhook-parse path, rather than introduce a second near-identical session DTO.

**Application — repository (`IStoreRepository` / `StoreRepository`)**
- `Task<IReadOnlyList<StoreRecordedStripePayment>> GetRecordedStripePaymentsAsync(CancellationToken ct = default)`
  returning `(string PaymentIntentId, Guid OrderId, decimal AmountEur, Instant ReceivedAt)` for
  rows where `StripePaymentIntentId != null`. Feeds both the missing-detection set and orphan detection.
  Rejected `GetRecordedStripePaymentIntentIdsAsync` (set only) because orphan rows need order/amount to display.

**Application — service (`IStoreService` / `StoreService`)**
- `Task<StripeReconciliationReport> GetStripeReconciliationAsync(CancellationToken ct = default)`
  — builds the rows + health flags + counts; resolves matched-order labels via the repo
  (`GetOrderByIdAsync`) for the distinct matched ids.
- `Task<StripeReconciliationResult> RecordMissingStripePaymentsAsync(Guid actorUserId, CancellationToken ct = default)`
  — re-pulls the Stripe list (never trusts client-posted amounts), records every
  paid+matched+unrecorded session via the existing **idempotent** `RecordStripePaymentAsync`,
  writes one `StorePaymentsReconciled` summary audit entry (plus the per-payment audit each
  record already emits). Team-owned orders are skipped (defense-in-depth already in
  `RecordStripePaymentAsync`).
- New read-model records (Application, no EF types): `StripeReconciliationReport`
  (`WebhookConfigured`, `CheckoutConfigured`, `Rows`, `Orphans`, count summary),
  `StripeReconciliationRow` (session/PI id, amount, status enum, created, OrderId?, OrderLabel?),
  `StripeReconciliationResult` (`RecordedCount`, `TotalEur`).

**Web (`StoreAdminController`, route `Store/Admin`, policy `StoreCatalogAdmin`)**
- `GET Store/Admin/Payments` → `GetStripeReconciliationAsync` → `Views/StoreAdmin/Payments.cshtml`.
- `POST Store/Admin/Payments/RecordMissing` `[ValidateAntiForgeryToken]` → `RecordMissingStripePaymentsAsync`
  → redirect back with a success summary.
- `StorePaymentsReconciliationViewModel`; controller does formatting/ordering only.
- Nav link "Payments" added to the Store admin area alongside Catalog / Summary.

**Forward fix (in scope — so this can't recur invisibly)**
- `CreateCheckoutSessionAsync`: also set `PaymentIntentData.Metadata["humans_store_order_id"]`
  and a legible description (order ref + counterparty), so the PI itself carries the join key
  and the Stripe dashboard / customer receipt stop reading "2026 - Yes".

## Data flow

```
GET /Store/Admin/Payments
  StripeService.ListStoreCheckoutSessionsAsync (StoreKey, auto-paged)  ─┐
  StoreRepository.GetRecordedStripePaymentsAsync ─────────────────────┐ │
  StoreService diffs by PI id, resolves order labels  <───────────────┴─┘
  → health banner + rows (Recorded/Missing/Unmatched/Unpaid) + orphans

POST /Store/Admin/Payments/RecordMissing
  re-pull Stripe list → for each paid+matched+unrecorded:
    RecordStripePaymentAsync(orderId, piId, amountEur)   // idempotent on PI id
  → one StorePaymentsReconciled audit + per-payment audits → summary
```

## Safety / invariants

- **Idempotent**: recording guards on unique PI id (`StripePaymentIntentExistsAsync`); the
  screen and the record action are safe to run repeatedly.
- **No fabricated data**: amount + PI id come from Stripe. Unmatched/orphan rows are reported,
  never recorded or deleted.
- **No hand-editing runtime state**: recovery flows through the normal service path with full audit.
- **Never blocks anything**: read-only screen + additive recording; no impact on checkout or sign-in.

## Testing

- Service recon logic (fake `IStripeService` + `IStoreRepository`): each status classification
  (recorded / missing / unmatched / unpaid), orphan detection, label resolution.
- `RecordMissingStripePaymentsAsync`: records only paid+matched+unrecorded; idempotent on
  re-run; skips Team orders; correct count/total + audit.
- Connector mapping (`payment_status`, metadata order id, amount minor-unit conversion) covered
  by mapping the SDK `Session` → `StoreCheckoutSessionData`.
- Architecture tests stay green (no new cross-section refs, no SDK types across the seam).
