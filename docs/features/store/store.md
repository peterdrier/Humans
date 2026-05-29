<!-- freshness:triggers
  src/Humans.Application/Services/Store/**
  src/Humans.Application/Interfaces/Store/**
  src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs
  src/Humans.Domain/Entities/Store*.cs
  src/Humans.Domain/Enums/Store*.cs
  src/Humans.Infrastructure/Data/Configurations/Store/**
  src/Humans.Infrastructure/Repositories/Store/**
  src/Humans.Infrastructure/Services/Stripe*.cs
  src/Humans.Infrastructure/Services/StoreWebhookRegistrationService.cs
  src/Humans.Web/Controllers/Store*.cs
  src/Humans.Web/Authorization/Requirements/StoreOrder*.cs
  src/Humans.Web/Views/Store/**
  src/Humans.Web/Views/StoreAdmin/**
-->
<!-- freshness:flag-on-change
  Store catalog editing, order lifecycle, OrderableUntil gate, Stripe Checkout flow, webhook ingestion, invoice issuance idempotency, treasury sync matching, and resource-based authorization — review when Store services/entities/controllers/auth handlers change.
-->

# 30 — Store

## Business Context

The Store section lets Camp Leads order infrastructure-as-a-service items from the collective for the year (containers, electrical hookups, generator hours, etc.) and pay against those orders incrementally as the camp budget firms up. Historically every camp's purchase ran through ad-hoc spreadsheets, WhatsApp threads, and a single Treasurer who had to chase Camp Leads for line-item confirmation and chase Holded into emitting one consolidated factura per camp at year-end. The Store section replaces that workflow with: a section-owned product catalog (priced once per year by `StoreAdmin`), a per-camp running tab of order lines snapshotted at add-time, multi-method payments (Stripe Checkout for cards, manual entries for bank transfer / cash) accumulating against the order, and a single Holded-issued factura emitted by `FinanceAdmin` once the camp finishes and all reconciliation is done.

This is fundamentally **camp data** with provenance recording (per `memory/architecture/provenance-fks-not-user-scoped.md`): order lines, payments, and invoices belong to the `CampSeason`, not to the lead who clicked the button. The `AddedByUserId` / `RecordedByUserId` / `IssuedByUserId` columns are audit/provenance only — deleting a user does not delete the order data.

Refunds, payouts, and chargebacks remain Stripe-dashboard-manual (per `memory/architecture/refunds-manual-via-dashboard.md`). Humans only does the bookkeeping side: a refund issued in Stripe gets recorded as a negative `StorePayment` row by the Treasurer.

The full architecture spec lives at [`docs/superpowers/specs/2026-04-30-store-section-design.md`](../../superpowers/specs/2026-04-30-store-section-design.md); the section invariant doc is [`docs/sections/Store.md`](../../sections/Store.md).

## User Stories

### US-30.1: Manage the Store Catalog (StoreAdmin)

**As** a `StoreAdmin` (or `FinanceAdmin` / `Admin`), **I want** to publish, edit, and deactivate Store products for the active year, **so that** Camp Leads can order against an authoritative price list.

**Acceptance Criteria:**
- `/Store/Admin/Catalog` lists every product for the current event year (active and inactive) with name, description, unit price, VAT rate, optional deposit, OrderableUntil deadline, and IsActive state.
- Create/edit share a single action at `/Store/Admin/Catalog/Edit` (no id = create, `/Store/Admin/Catalog/Edit/{id}` = edit). Submit posts to `/Store/Admin/Catalog/Save`. Trim+validate name (≤200), description (≤2000), non-negative numerics, VAT rate 0–100. `OrderableUntil` accepts any date — past, present, or future. The runtime guard at `StoreService.AddLineAsync` rejects new lines once today's event-zone date has passed it, so a date in the past simply makes the product no longer orderable; admins can extend or shorten it freely.
- "Deactivate" button performs a soft-deactivate (sets `IsActive = false`); never a hard delete. Deactivated products are hidden from the Camp Lead catalog view immediately, and `StoreService.AddLineAsync` rejects new lines against them with a clear error.
- The active year is derived via `IShiftManagementService.GetActiveAsync()` (see Cross-Section Dependencies in `docs/sections/Store.md`).
- Audit-logged: `StoreProductCreated`, `StoreProductUpdated`, `StoreProductDeactivated` with the actor user id.

### US-30.2: Build a Camp's Running Tab (Camp Lead)

**As** a Camp Lead, **I want** to add lines to my camp's Store order over the season as needs firm up, **so that** the camp's accumulating purchases are tracked in one ledger that maps directly to a single year-end factura.

**Acceptance Criteria:**
- `/Store` shows the lead's camp seasons for the active year (resolved via `ICampServiceRead.GetCampsForYearAsync`, scanning each camp's `GetLeadSeasonIdForYear`) with a list of orders for each.
- "Create order" creates a new `StoreOrder` in `Open` state attached to the camp season; multiple orders per season are allowed and disambiguated by an optional `Label`.
- Order detail at `/Store/Order/{id}` shows the line list, payment list, running balance, and counterparty fields.
- Add-line form posts to `/Store/Order/{id}/AddLine` with a product id and quantity. The line snapshots `UnitPriceSnapshot`, `VatRateSnapshot`, and `DepositAmountSnapshot` from the product at add-time — later catalog edits never mutate existing lines (`docs/sections/Store.md`).
- `AddLineAsync` rejects with a clear message if (a) the order is not `Open`, (b) the product is deactivated, or (c) `LocalDate.FromDateTime(today, eventTimeZone)` is past the product's `OrderableUntil` deadline.
- Remove-line form posts to `/Store/Order/{id}/RemoveLine` and is gated identically to AddLine on order state.
- Counterparty fields (name, VAT id, address, country code, email) are editable while the order is `Open`; `FinanceAdmin` can edit them in any state.
- Audit-logged: `StoreOrderCreated`, `StoreLineAdded`, `StoreLineRemoved`, `StoreCounterpartyEdited`.

### US-30.3: Pay Online via Stripe Checkout (Camp Lead)

**As** a Camp Lead, **I want** to pay against my order with a card through Stripe Checkout, **so that** I don't have to wait for a bank transfer to clear and the Treasurer doesn't have to manually reconcile the payment.

**Acceptance Criteria:**
- Pay form is rendered when the order has `Balance > 0`, the lead has `Pay` authorization, and Stripe is configured (`STRIPE_STORE_KEY` set). The amount input defaults to the balance owed and is capped at it.
- POST to `/Store/Order/{id}/Pay` calls `IStripeService.CreateCheckoutSessionAsync` with `humans_store_order_id` metadata, EUR-cents amount (rounded `MidpointRounding.AwayFromZero`), success and cancel URLs (absolute, scheme + host from request), and the lead's email if available; redirects the browser to Stripe's hosted checkout.
- Stripe's POST to `/Store/StripeWebhook` is verified via `EventUtility.ConstructEvent(body, signature, signingSecret, throwOnApiVersionMismatch: false)`. The endpoint is subscribed to all four `checkout.session.*` events; today only `completed` triggers `StorePayment` recording, and `async_payment_succeeded` / `async_payment_failed` / `expired` log at Warning + return 200 (state-machine handler pending). Unrelated event types log at Debug + return 200.
- The handler extracts `humans_store_order_id` metadata, the `PaymentIntentId`, and `AmountTotal / 100m`, then calls `IStoreService.RecordStripePaymentAsync`. The service is idempotent on `paymentIntentId` — duplicate webhook deliveries record nothing.
- Successful Stripe payments insert a `StorePayment` row with `Method = Stripe`, `RecordedByUserId = null`, audit-logged with job actor `"StripeWebhook"`.
- Pay is allowed regardless of order state (payments continue after invoice issuance — see `StoreOrderOperationRequirement.Pay`).
- Webhook errors are logged but the controller returns 200 to prevent Stripe retry storms; signature failures return 400.

### US-30.4: Record a Manual Payment (Treasurer)

**As** a `FinanceAdmin`, **I want** to record bank transfers, cash receipts, and Stripe-dashboard refunds against an order, **so that** the order's balance reflects every euro that has actually moved.

**Acceptance Criteria:**
- POST to `/Store/Order/{id}/RecordPayment` with amount (signed — negatives are refunds), method (`BankTransfer` | `Manual`), optional external reference (e.g. Holded treasury entry id), and optional notes.
- Inserts a `StorePayment` row with `RecordedByUserId = actorUserId`, `Method` as supplied. `Stripe` method is reserved for the webhook path and rejected here.
- Allowed in any order state (refunds frequently happen post-issuance).
- Audit-logged with the actor.

### US-30.5: Issue the Consolidated Factura (Treasurer)

**As** a `FinanceAdmin`, **I want** to emit one Holded factura per order once the camp's purchases are finalized, **so that** the camp gets a single consolidated invoice rather than line-item-by-line-item paperwork.

**Acceptance Criteria:**
- POST to `/Store/Order/{id}/IssueInvoice` validates: order must be `Open`, must have at least one line, counterparty fields must all be present, `IssuedInvoiceId` must be null.
- Calls Holded's invoice-create API with the lines + counterparty and writes a `StoreInvoice` row with `OrderId`, `HoldedDocId`, `HoldedDocNumber`, `IssuedAt`, `IssuedByUserId`, plus the full request and response payloads (jsonb) for audit.
- Sets the order's `State = InvoiceIssued` and `IssuedInvoiceId` to the new invoice id.
- Idempotent: re-posting against an already-issued order returns 409 / clear error; partial failures (Holded returns success but DB write fails, or vice versa) recover deterministically — see the design spec for the exact protocol.
- *Note: implementation of US-30.5 is paused — Holded integration is scheduled for a follow-up PR ~1 month out.*

### US-30.6: Treasury Sync (Background)

**As** the system, **I want** to poll Holded for treasury entries that match outstanding `BankTransfer` payments, **so that** the order ledger stays in sync with what actually cleared the bank without Treasurer manual reconciliation.

**Acceptance Criteria:**
- `StoreTreasurySyncState` is a singleton cursor row tracking `LastSyncAt`, `SyncStatus` (`Idle` / `Running` / `Failed`), and `LastError`.
- The sync job (paused — Phase 7) polls Holded for new treasury entries since `LastSyncAt`, attempts to match them to outstanding `BankTransfer` `StorePayment` rows by amount + counterparty, and stores match results.
- Unmatched entries are flagged for Treasurer attention.
- *Note: implementation of US-30.6 is paused alongside US-30.5.*

### US-30.7: Admin Aggregate Summary (StoreAdmin / FinanceAdmin / Admin)

**As** a `StoreAdmin`, `FinanceAdmin`, or `Admin`, **I want** a single page that aggregates a year's store activity across all camps, **so that** I can read top-line totals, spot under-paid camps, and audit per-product demand without opening order pages one by one.

**Acceptance Criteria:**
- `/Store/Admin/Summary` is gated by `PolicyNames.StoreCatalogAdmin` (the same policy as `/Store/Admin/Catalog` and `/Store/Admin/Orders`); volunteers receive 403/redirect.
- Year selector at the top defaults to the active event year via `IShiftManagementService.GetActiveAsync()`, falling back to the clock's current year if there is no active event. `?year=N` overrides the default.
- Three projections render in this order, each as its own card:
  - **By-camp** — one row per order with Camp / Label / State / Total due / Paid / Balance. Camp name links to `/Store/Order/{id}`. Columns are client-side sortable. A paid-status dropdown (All / Paid / Partial / Unpaid) filters rows in-place; classification rule: `Balance ≤ 0` → paid, else `Paid > 0` → partial, else unpaid.
  - **By-item** — one row per product (qty, revenue €), including deactivated products that still have lines in the year.
  - **Cross-tab** — camps × products matrix with qty cells (blank for 0), row totals, column totals, grand total. Both axes alphabetical. Column totals are consistent with by-item totals.
- Service surface: `IStoreService.GetStoreSummaryAsync(int year, CancellationToken)` returns a `StoreSummaryDto` composing the three projections. Replaces the earlier `GetAllOrderSummariesAsync` stub (no production callers existed).
- Single repository round-trip via `IStoreRepository.GetOrdersForCampSeasonsWithLinesAndPaymentsAsync` (orders with `Lines + Payments` eager-loaded), plus one camp-name batch via `ICampServiceRead.GetCampsForYearAsync` (projecting each camp's seasons for the year) and one product fetch via `GetAllProductsForYearAsync`. All aggregation is in-memory per the §Scale-and-Deployment rule in `CLAUDE.md`.
- Reachable from the admin nav under Money → "Store summary" (gated by the same policy so it stays hidden for non-admins).

## Data Model

See [`docs/sections/Store.md`](../../sections/Store.md) for the full table schema. Summary:

| Entity | Table | Aggregate root | Cross-section linkage |
|---|---|---|---|
| `StoreProduct` | `store_products` | yes | none |
| `StoreOrder` | `store_orders` | yes | `CampSeasonId` (bare Guid → Camps) |
| `StoreOrderLine` | `store_order_lines` | child of `StoreOrder` | `ProductId` (intra), `AddedByUserId` (provenance) |
| `StorePayment` | `store_payments` | child of `StoreOrder` | `RecordedByUserId` (provenance, nullable for Stripe) |
| `StoreInvoice` | `store_invoices` | yes (one per order) | `OrderId` (intra), `IssuedByUserId` (provenance) |
| `StoreTreasurySyncState` | `store_treasury_sync_state` | singleton (`Id = 1`) | none |

All cross-section linkage is bare `Guid` columns — no FK constraints, no nav properties (per `memory/architecture/no-cross-section-ef-joins.md`).

## Workflow / State Machines

### StoreOrder lifecycle

```
                  AddLine / RemoveLine / EditCounterparty (Lead, Open only)
                  RecordPayment (FinanceAdmin, any state)
                  Pay (Lead, any state — Stripe Checkout)
                  ▲
                  │
            ┌─────┴──────┐
   create   │            │   IssueInvoice
──────────► │   Open     │ ────────────────► InvoiceIssued
            │            │                          │
            └────────────┘                          │
                                                    │
                  RecordPayment / Pay continue ─────┘
```

- **Open**: lines/counterparty editable by the Camp Lead; `FinanceAdmin` can edit counterparty regardless of state. Payments accumulate.
- **InvoiceIssued**: the consolidated factura has been emitted to Holded. Lines and counterparty are frozen at the auth layer for Camp Leads. Payments continue (refunds, late bank transfers, post-issuance Stripe captures all hit the same row).

## Authorization

Resource-based via `StoreOrderAuthorizationHandler` keyed on `StoreOrderOperationRequirement` ({`View`, `Create`, `AddLine`, `RemoveLine`, `EditCounterparty`, `Pay`}). Logic:

| Role | View | AddLine / RemoveLine / EditCounterparty | Pay |
|---|---|---|---|
| `Admin` / `StoreAdmin` / `FinanceAdmin` | any state | any state | any state |
| Camp lead/co-lead of the camp owning the order's `CampSeason` | any state | `Open` only | any state |
| Anyone else | denied | denied | denied |

`StoreAdmin` is the Store-domain superset (per `memory/code/admin-role-superset.md`): full access to catalog, orders, payments, and invoices. `FinanceAdmin` retains parallel access for accounting workflows. The shared check is `RoleChecks.CanAdministerStore`.

## Cross-Section Dependencies

| Dependency | Used for |
|---|---|
| `ICampServiceRead` | resolve current user's lead camp season for the active year, fetch season name |
| `IShiftManagementService` | derive the active event year + time zone for OrderableUntil deadline gate |
| `IAuditLogService` | audit every write |
| `IStripeService` | Checkout Session creation; webhook signature verification |
| `IHoldedClient` | factura issuance + treasury sync (Phase 5/7, paused) |

## Configuration

The Store section reads several environment variables — see `docs/sections/Store.md` *Stripe Configuration* for the canonical contract. Summary:

- `STRIPE_STORE_KEY` — production-narrow Restricted API Key (`rk_*`) with `checkout_session:write` scope only.
- `STRIPE_STORE_WEBHOOK_SECRET` — webhook signing secret. Set manually in QA/prod; auto-stamped in PR-preview by the registrar.
- `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` — broader-scope key used by `StoreWebhookRegistrationService` to auto-register PR-preview webhooks. **Only set in PR-preview environments.**
- `Stripe:WebhookCleanupOwner` / `Stripe:WebhookCleanupRepository` — GitHub fork to query for currently-open PRs during cross-PR webhook sweep.

## Related Features / Sections

- [`docs/sections/Store.md`](../../sections/Store.md) — section invariants (canonical reference)
- [`docs/superpowers/specs/2026-04-30-store-section-design.md`](../../superpowers/specs/2026-04-30-store-section-design.md) — full architecture / phasing spec
- [`memory/architecture/refunds-manual-via-dashboard.md`](../../../memory/architecture/refunds-manual-via-dashboard.md) — money-out is dashboard-manual
- [`memory/code/stripe-restricted-keys.md`](../../../memory/code/stripe-restricted-keys.md) — production keys must be RAKs
- [`memory/architecture/provenance-fks-not-user-scoped.md`](../../../memory/architecture/provenance-fks-not-user-scoped.md) — Store FKs are provenance, not user-scoped data
- `20-camps.md` — `CampSeason` is the order's owning aggregate
- `24-ticket-vendor-integration.md` — sibling Stripe integration; same RAK + dashboard-only-refunds discipline
