# Store section — design

**Status:** Draft, brainstorm complete, awaiting plan.
**Date:** 2026-04-30
**Owner:** Peter Drier

## Purpose

A new `/Store` section that lets camps (Barrios) buy items from the org for the event year. Sellable items are configured by `StoreAdmin` (catalog of ~15 SKUs). Orders accumulate per camp during the buy window, payments come in via Stripe and bank transfer (multiple per order), and a single consolidated factura is pushed to Holded post-event by `FinanceAdmin`.

This is a new section. It does not extend or replace anything existing.

## Glossary

- **Order** — A camp's collection of purchased lines, payments, counterparty data, and (eventually) one issued invoice. Multiple orders per camp-season are allowed but rare; one is the dominant case. Optional `Label` distinguishes split orders (e.g. `Foo-A`, `Foo-B`).
- **Line** — A single product purchase within an order. Snapshots price/VAT/deposit at the time it was added so later catalogue edits don't retroactively change posted lines.
- **Payment** — A single inbound payment event. Signed amount: positive = received, negative = refund. Method ∈ {Stripe, BankTransfer, Manual}.
- **Invoice** — The Holded factura issued at the end of the order's lifecycle. One per Order.
- **Buy window** — The period during which lines can be added/removed. Per-product (`Product.OrderableUntil`), not per-season.
- **Counterparty** — Camp-supplied billing entity (name, optional VAT-id, address, country, email). 75% of camps are non-Spanish; all goods are physically delivered on-site in Spain so place-of-supply is Spain regardless of buyer country.

## Scope

In:
- Catalog management (StoreAdmin).
- Per-camp ordering with running balance (Camp Lead).
- Stripe Checkout payment flow + webhook ingestion.
- Manual bank-transfer payment entry (FinanceAdmin).
- Best-effort auto-match of Holded treasury entries to orders by description / `Order.Label`.
- Consolidated invoice issuance to Holded post-event.
- Per-barrio + per-item summary view for admins.

Out (deferred / non-goals):
- Structured deposit-return UI. Treasurer handles propane returns out-of-band and adjusts via negative `StorePayment` rows before issuing the invoice.
- `OrderableUntil` admin override (no late-additions back door in v1).
- B2B reverse-charge VAT. Place of supply is Spain, full Spanish VAT applies.
- Stock / availability caps. Unlimited per item; org sources to aggregate demand.
- Notifications beyond the ones already produced by AuditLog.

## Decisions log (from brainstorm)

| Topic | Decision |
|---|---|
| Buyer / counterparty | Camp-supplied. Provided when the camp wants a personal-NIF factura. Optional in v1 — fall back to `Camp.Name` when absent. (Re-revisit if Peter pivots.) |
| Invoice lifecycle | DB is source of truth during the buy window. Holded is written to once per Order, post-event, by FinanceAdmin clicking "Issue invoice". |
| Editing | Free add/remove pre-cutoff per product; per-product cutoff (`OrderableUntil`); no admin override v1. |
| Payment flow | Running per-Order balance, multi-method, multi-payment, decoupled from individual lines. |
| VAT | Per-item rate (default 21%), charged to all camps regardless of country. Lower rates configurable (food may be 10% or 4%). |
| Stock | Unlimited. |
| Deposit | Type A only (refundable security deposit, e.g. propane containers). Not VAT-able. Returns handled out-of-band in v1. |
| Multiple orders per camp | Allowed. Each carries its own counterparty and gets its own invoice. |
| Bank-transfer ingestion | Best-effort auto-match from Holded treasury, fall back to manual entry if matching turns out gnarly during build. |
| URL convention | `/Store/Admin/*`, not `/Admin/Store/*`. |

## Architecture

### Section ownership

New `Store` section. Owns: `StoreProduct`, `StoreOrder`, `StoreOrderLine`, `StorePayment`, `StoreInvoice`, plus a sync-cursor singleton for Holded treasury polling.

Service: `IStoreService` + `IStoreRepository` (thick repo, materializes results — no LINQ-on-DbSet leakage outside the repo).

Foundational sections (Users, Profiles, Camps, Auth) are called *into* by Store, never the reverse. Audit-log emissions for line/payment/invoice events.

### Connector ownership

- **Stripe** — existing `IStripeService` extended with `CreateCheckoutSessionAsync` plus a webhook handler. Continues to also serve Tickets' read-only enrichment. Single connector class.
- **Holded** — existing `HoldedClient` extended with `UpsertContactAsync`, `CreateInvoiceAsync`, and `ListTreasuryEntriesAsync`. Inbound (Finance) flow unchanged. Don't split inbound/outbound until the surface area shows whether it's worth it.

### Layering

| Layer | Components |
|---|---|
| `Humans.Domain` | Entities (`StoreProduct`, `StoreOrder`, `StoreOrderLine`, `StorePayment`, `StoreInvoice`, `StoreTreasurySyncState`). Enums (`StoreOrderState`, `StorePaymentMethod`). Constants (`RoleNames.StoreAdmin`). |
| `Humans.Application` | `IStoreService`, `IStoreRepository`, DTOs (`OrderDto`, `OrderLineDto`, `OrderSummaryDto`, etc.). `IStripeService` extension. New `IHoldedSalesInvoiceClient`-shaped methods on the existing Holded interface (or kept on the same client; choose during planning based on how many methods land). |
| `Humans.Infrastructure` | `StoreRepository`, EF configurations, `StoreService` impl, treasury-sync `StoreTreasurySyncJob` (Hangfire). Extensions to `HoldedClient` and `StripeService`. |
| `Humans.Web` | `StoreController`, `StoreAdminController`, `StoreSummaryController`, `StoreStripeWebhookController`. Resource-based authz handlers. Razor views. Nav links. |

## Data model

All fields use NodaTime for time types per the codebase rule. Money is `decimal` EUR. No concurrency tokens. No `Cached*` types — caching is transparent.

### `StoreProduct`

Catalogue. ~15 active rows per event year.

| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| YearHandle | TBD | Year the product belongs to. Resolution between `CampSettings.PublicYear`, `CampSeason.Year`, or a new `Year` entity is deferred to planning. |
| Name | string | Required. |
| Description | string | Required. |
| UnitPriceEur | decimal | Required. |
| VatRatePercent | decimal | Required. Default 21, configurable per item. |
| DepositAmountEur | decimal? | Nullable. Refundable security deposit per unit. |
| OrderableUntil | LocalDate | Per-item buy-window deadline. |
| IsActive | bool | Soft-disable. |
| CreatedAt / UpdatedAt | Instant | |

### `StoreOrder`

One per "purchase context" within a camp-season. Multiple per CampSeason allowed; no uniqueness constraint.

| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| CampSeasonId | Guid | FK to existing `CampSeason`. Cross-domain — keep as FK only, no nav (matches design-rules §15). |
| Label | string? | Optional. e.g. `Foo-A`, `Foo-B`. Blank in dominant single-order case. Used as bank-transfer description match key. |
| State | `StoreOrderState` | `Open` \| `InvoiceIssued` |
| CounterpartyName | string? | Camp-supplied. Falls back to camp name at issue time if blank. |
| CounterpartyVatId | string? | EU VAT-ID or NIF. Optional. |
| CounterpartyAddress | string? | |
| CounterpartyCountryCode | string? | ISO 3166-1 alpha-2. |
| CounterpartyEmail | string? | |
| IssuedInvoiceId | Guid? | FK to `StoreInvoice` once issued. |
| CreatedAt / UpdatedAt | Instant | |

### `StoreOrderLine`

A purchase line. Destructively delete pre-cutoff; AuditLog captures the change.

| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| OrderId | Guid | FK |
| ProductId | Guid | FK |
| Qty | int | |
| UnitPriceSnapshot | decimal | Captured at add time. |
| VatRateSnapshot | decimal | Captured at add time. |
| DepositAmountSnapshot | decimal? | Captured at add time. |
| AddedAt | Instant | |
| AddedByUserId | Guid | |

### `StorePayment`

A single payment event against an Order. Signed; negative = refund.

| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| OrderId | Guid | FK |
| AmountEur | decimal | Signed. |
| Method | `StorePaymentMethod` | `Stripe` \| `BankTransfer` \| `Manual` |
| StripePaymentIntentId | string? | Set for Stripe. |
| ExternalRef | string? | Holded payment id (treasury sync) or bank reference (manual). |
| ReceivedAt | Instant | |
| RecordedByUserId | Guid? | Null when sourced from webhook/treasury sync. |
| Notes | string? | Optional FinanceAdmin annotation. |

### `StoreInvoice`

| Field | Type | Notes |
|---|---|---|
| Id | Guid | PK |
| OrderId | Guid | FK, unique. |
| HoldedDocId | string | Returned from Holded create-invoice POST. |
| HoldedDocNumber | string | e.g. `F260123` |
| IssuedAt | Instant | |
| IssuedByUserId | Guid | |
| RequestPayload | jsonb | What we POSTed. |
| ResponsePayload | jsonb | What Holded returned. |

### `StoreTreasurySyncState`

Singleton (Id = 1). Tracks Holded treasury polling cursor.

| Field | Type | Notes |
|---|---|---|
| Id | int | Always 1. |
| LastSyncAt | Instant? | Cursor passed to Holded `since` param. |
| SyncStatus | `SyncStatus` | `Idle` / `Running` / `Error` |
| LastError | string? | |

### Computed: per-Order balance

Not stored. Pure function over an Order's lines and payments:

```
balance = SUM(line.Qty × line.UnitPriceSnapshot × (1 + line.VatRateSnapshot/100))
        + SUM(line.Qty × line.DepositAmountSnapshot)
        − SUM(payment.AmountEur)
```

Computed in `StoreService` on read. Cache transparently per-order if hot-path measurements warrant.

## Lifecycle

### Order state machine

```
[Open] ──FinanceAdmin "Issue invoice"──▶ [InvoiceIssued] (terminal)
   ↑
   └── lines added/removed; payments recorded; counterparty edited
```

After issuance, **lines and counterparty are read-only**, but **payments continue to be accepted** (Stripe Checkout, treasury auto-match, manual entry, refunds) so late settlements remain visible in the running balance. Adding more *items* to that camp = a new Order.

### Per-product gating

Adding a line: rejected if `now > Product.OrderableUntil` for that product.
Removing a line: same rule.
Both apply equally to Camp Lead and FinanceAdmin in v1.

### Payment recording

- Stripe → camp lead clicks "Pay €X via Stripe" → `StripeService.CreateCheckoutSessionAsync` → redirect → `checkout.session.completed` webhook → insert `StorePayment` (Method=Stripe, StripePaymentIntentId, RecordedByUserId=null).
- Bank transfer (auto, best-effort) → `StoreTreasurySyncJob` polls Holded treasury since `LastSyncAt`, attempts to match incoming entries by description → `StoreOrder.Label`. On match → insert `StorePayment` (Method=BankTransfer, ExternalRef=Holded id). Unmatched entries left alone for FinanceAdmin to handle.
- Bank transfer (manual) → FinanceAdmin enters payment via admin UI.
- Refund → FinanceAdmin enters a negative `StorePayment` (Method=Stripe with refunded PI id, or BankTransfer/Manual).

### Invoice issuance

FinanceAdmin clicks "Issue invoice" on a per-Order admin view (or "Issue all" on the index).

1. Validate: Order is `Open`. (Validation only — no startup-guard pattern.)
2. Resolve effective counterparty: use `CounterpartyName` if set, else `Camp.Name`. VAT-ID, address, country, email: take from Order's counterparty fields if set.
3. `HoldedClient.UpsertContactAsync(counterparty)` → returns Holded contact id. Match strategy: VAT-id when present, else name + country.
4. Build invoice payload from lines:
   - One taxable line per `StoreOrderLine`: `name = product.Name`, `units = qty`, `price = UnitPriceSnapshot`, `tax = VatRateSnapshot`.
   - One VAT-free line per line that has a deposit: `name = "Depósito {product.Name}"`, `units = qty`, `price = DepositAmountSnapshot`, `tax = 0`.
5. `HoldedClient.CreateInvoiceAsync(payload)` → returns `HoldedDocId` + `HoldedDocNumber`.
6. Insert `StoreInvoice`. Update `Order.State = InvoiceIssued`, `Order.IssuedInvoiceId = newInvoice.Id`.
7. AuditLog: `StoreInvoiceIssued`.

Failure between steps 3–5 leaves the Order in `Open` state; user can retry. Idempotency check: if `Order.IssuedInvoiceId` already set, refuse to re-issue.

## ⚠ Holded outbound — thin-probe caveat

The probe at `.holded-probes.md` returned **two** purchase docs (incoming-direction) and **zero** invoice docs (outgoing-direction). The outbound `invoice` document shape is not yet known from real data. Per the project rule on not speccing entities from a thin probe:

**Pre-implementation step (mandatory, before writing the Holded extensions):**

1. Manually create one test invoice in Holded UI with a multi-line item including a deposit-style line.
2. Manually create or trigger one incoming bank-transfer entry visible in Holded treasury.
3. Dump both via the Holded API (`GET /invoicing/v1/documents/invoice/{id}`, `GET /invoicing/v1/treasury`) and commit the JSON samples alongside `.holded-doc-sample.json`.
4. Lock the field mapping in this design's "Invoice issuance" step against the real shape, not the field-name list from the purchase probe.

The contact-upsert endpoint also needs the same treatment — verify the request body fields against a real Holded contact JSON before locking.

## Stripe write surface

Extend `IStripeService`:

```csharp
Task<string> CreateCheckoutSessionAsync(
    Guid storeOrderId,
    decimal amountEur,
    string successUrl,
    string cancelUrl,
    string? customerEmail,
    CancellationToken ct = default);
```

Returns the Stripe-hosted Checkout Session URL. Metadata on the session:
- `humans_store_order_id` = `storeOrderId.ToString()`

Webhook controller:
- Route: `POST /Store/StripeWebhook`
- `[AllowAnonymous]`, signature verified with `STRIPE_WEBHOOK_SECRET` env var (mirroring existing Stripe config pattern).
- Handles `checkout.session.completed`. Looks up StoreOrder via metadata, checks for duplicate webhook by `StripePaymentIntentId`, inserts `StorePayment`.

## Authorization

Resource-based authz handlers per the design-rules pattern. No `isPrivileged` boolean smuggling.

| Actor | Scope |
|---|---|
| Camp Primary Lead, CoLeads (existing `CampLead` entity) | View their own camp's orders. Add/remove lines (subject to `OrderableUntil`). Initiate Stripe Checkout. Edit counterparty data on their own camp's Open orders. |
| `StoreAdmin` (new role) | Manage product catalog. View `/Store/Summary`. |
| `FinanceAdmin` (existing role) | View all camps' orders. Manual bank-transfer entry. Refunds. Edit counterparty (any camp). Issue invoices. Run/observe treasury sync. View `/Store/Summary`. |
| `Admin` (existing) | Superset. |
| Anonymous (signed) | Stripe webhook only. |

`RoleNames.StoreAdmin = "StoreAdmin"` added to `Humans.Domain.Constants.RoleNames`.

## URL surface

| URL | Audience |
|---|---|
| `/Store` | Camp Lead — catalog browse, their camp's orders, balance, Pay button, "Start order" / "Split order". |
| `/Store/Order/{id}` | Camp Lead (own camp) + admin roles — order detail (lines, payments, balance). |
| `/Store/Admin/Catalog` | StoreAdmin — product CRUD. |
| `/Store/Admin/Orders` | FinanceAdmin — all-camps order index, per-Order ledger view, payment entry, refund, "Issue invoice" + "Issue all", treasury-sync status panel. |
| `/Store/Summary` | StoreAdmin / FinanceAdmin / Admin — per-barrio rows + per-item rows overview. Specific metrics finalized during implementation. |
| `/Store/StripeWebhook` | Stripe (signature-verified). |

All new pages get nav links per the no-orphan-pages rule. Camp Lead view in main nav (daily traffic during the buy window). Admin pages in section's admin nav.

## Cross-section dependencies

Store calls into:
- `ICampService` — resolve a user's active camps + Camp Primary/CoLead role check.
- `IUserService` — display names for audit-log rendering.
- `IStripeService` (extended) — Checkout Session creation.
- `HoldedClient` (extended) — contact upsert, invoice POST, treasury list.
- `IAuditLogService` — emit Store* events.

Nothing in the codebase calls Store. Store is a leaf consumer of foundational sections.

## Audit log events

Append to existing `audit_log_entries` via `IAuditLogService`:

- `StoreLineAdded` — actor=who, subject=camp, payload={orderId, productId, qty, snapshotPrice}.
- `StoreLineRemoved` — same shape.
- `StorePaymentRecorded` — actor=who-or-system, subject=camp, payload={orderId, amount, method, ref}.
- `StorePaymentRefunded` — same.
- `StoreCounterpartyEdited` — actor, subject=camp, payload=before/after diff.
- `StoreInvoiceIssued` — actor=FinanceAdmin, subject=camp, payload={orderId, holdedDocNumber}.
- `StoreProductCreated` / `StoreProductEdited` / `StoreProductDeactivated` — actor=StoreAdmin, payload=product diff.

The audit log is the concurrency safety net at this scale. No `IsConcurrencyToken` on Store entities.

## Section invariants (for `docs/sections/Store.md`)

To be created during implementation alongside the section:

- **Concepts** — Product, Order, Line, Payment, Invoice, Buy window, Counterparty.
- **Owned tables** — `store_products`, `store_orders`, `store_order_lines`, `store_payments`, `store_invoices`, `store_treasury_sync_state`.
- **Owned services** — `IStoreService`, `IStoreRepository`.
- **Invariants:**
  - An Order's `State` advances `Open → InvoiceIssued` and is terminal.
  - Lines snapshot price/VAT/deposit at add time.
  - Lines may not be added or removed past `Product.OrderableUntil`.
  - After issuance: lines + counterparty are read-only; payments continue.
  - All goods are delivered in Spain → Spanish VAT applies regardless of buyer country (no reverse-charge).
  - Refundable deposits are tracked on lines but appear as VAT-free lines on the issued invoice.
  - One invoice per Order, issued exactly once. Idempotent-on-retry.
  - Stripe webhook insertions are deduped by `StripePaymentIntentId`.
- **Negative access rules** — Camp Leads cannot see other camps' orders; cannot edit counterparty after issuance; cannot issue invoices; cannot manage catalog.
- **Cross-section dependencies** — `ICampService`, `IUserService`, `IStripeService`, `HoldedClient`, `IAuditLogService`.

## Open / deferred items

| Item | Resolution |
|---|---|
| Whether invoice is always issued or opt-in by camp | v1: always issued, fall back to camp name when counterparty blank. Re-revisit if Peter pivots. |
| Year handle for `StoreProduct` | Pick during planning between `CampSettings.PublicYear`, `CampSeason.Year`, or new `Year` entity. |
| Deposit-return UI | Out-of-band in v1. FinanceAdmin uses negative `StorePayment` to refund. |
| Admin override of `OrderableUntil` | Not in v1. |
| Outbound Holded `invoice` payload exact field shape | Pre-implementation richer probe required (see "Holded outbound — thin-probe caveat"). |
| `/Store/Summary` exact metrics | Finalized during implementation; design only commits to "per-barrio rows + per-item rows". |

## Notes on Spanish invoicing law (reference, not implementation)

- Place of supply for goods = where physically delivered. All event goods are handed over in Spain → Spanish VAT applies regardless of buyer EU/non-EU status.
- A factura must include buyer name + tax-id (NIF/CIF/EU VAT-ID) + address. When buyer doesn't supply these, Spanish "ticket simplificado" (B2C threshold) is the alternative — out of scope for v1; v1 falls back to camp name, which is sufficient for Holded but may not be a fully-compliant factura. FinanceAdmin's responsibility to ensure counterparty is filled where compliance matters.
- True security deposits (fianzas) for goods returned post-event are not subject to VAT. The deposit row on the invoice is VAT-free.
- Issuing a single consolidated factura covering supplies made on multiple dates is permitted within the same calendar month; cross-month consolidation requires the "factura recapitulativa" form which is also permitted but constrained to at-latest the 16th of the following month for B2B (looser for B2C). Issuing day-after-event for an event in summer easily fits these windows.

This is informational. Compliance verification with the org's accountant is recommended before first live use.
