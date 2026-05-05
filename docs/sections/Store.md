<!-- freshness:triggers
  src/Humans.Application/Services/Store/**
  src/Humans.Application/Interfaces/Store/**
  src/Humans.Application/Interfaces/Repositories/IStoreRepository.cs
  src/Humans.Application/Interfaces/IStripeService.cs
  src/Humans.Domain/Entities/Store*.cs
  src/Humans.Domain/Enums/Store*.cs
  src/Humans.Infrastructure/Data/Configurations/Store/**
  src/Humans.Infrastructure/Repositories/Store/**
  src/Humans.Infrastructure/Services/StripeService.cs
  src/Humans.Web/Controllers/Store*.cs
  src/Humans.Web/Authorization/Requirements/StoreOrder*.cs
-->
<!-- freshness:flag-on-change
  Store catalog editing, order lifecycle, OrderableUntil gate, invoice issuance idempotency, treasury sync matching, Stripe Checkout / webhook signature verification, and resource-based authorization — review when Store services/entities/controllers/auth handlers/Stripe surfaces change.
-->

# Store — Section Invariants

Per-camp catalog ordering, multi-method payments, and consolidated Holded factura issuance for Camp Lead purchases.

## Concepts

- A **Store Product** is a catalog item available to Camp Leads in a given event year (price, VAT rate, optional deposit, ordering deadline). Products are created and edited by StoreAdmin.
- A **Store Order** is a Camp Lead's order against a `CampSeason`. Lifecycle: **Open** → **InvoiceIssued**. Multiple orders per `CampSeason` are allowed, distinguished by an optional `Label`.
- A **Store Order Line** is a line on an order that snapshots the product's price, VAT, and deposit at the time the line was added — later catalog edits never mutate existing lines.
- A **Store Payment** is a payment against an order, recorded with one of three methods (`Stripe`, `BankTransfer`, `Manual`). Negative amounts represent refunds.
- A **Store Invoice** is the consolidated Holded factura issued for an order. One invoice per order, written once at issuance.
- A **Store Treasury Sync State** is the singleton cursor row that the treasury-sync job uses to track its last successful Holded poll.

## Data Model

### StoreProduct

Catalog item for a given event year.

**Table:** `store_products`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Year | int | Event year (plain int — no FK to CampSettings/CampSeason) |
| Name | string(200) | Required |
| Description | string(2000) | Required |
| UnitPriceEur | numeric(12,2) | |
| VatRatePercent | numeric(5,2) | |
| DepositAmountEur | numeric(12,2)? | Optional per-unit deposit |
| OrderableUntil | LocalDate | Add-line deadline |
| IsActive | bool | Soft-deactivate |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Indexes:** `(Year, IsActive)`.

### StoreOrder

A camp's order against a season.

**Table:** `store_orders`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid | FK only — no nav |
| Label | string(100)? | Optional disambiguator within a season |
| State | StoreOrderState (int) | Open or InvoiceIssued |
| CounterpartyName / VatId / Address / CountryCode / Email | string? | Editable by Camp Lead while Open; FinanceAdmin always |
| IssuedInvoiceId | Guid? | Set when invoice is issued |
| CreatedAt / UpdatedAt | Instant | |

**Indexes:** `CampSeasonId`, `State`.

**Cross-section linkage:** `CampSeasonId` is a bare `Guid` column — no FK constraint, no navigation property (per `memory/architecture/no-cross-section-ef-joins.md`). Resolved at the service layer via `ICampService.GetCampSeasonByIdAsync`.

**Aggregate-local navs:** `StoreOrder.Lines`, `StoreOrder.Payments`.

### StoreOrderLine

**Table:** `store_order_lines`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| OrderId | Guid | FK to store_orders, cascade delete |
| ProductId | Guid | FK only — no nav |
| Qty | int | |
| UnitPriceSnapshot | numeric(12,2) | Snapshot at add-time |
| VatRateSnapshot | numeric(5,2) | Snapshot at add-time |
| DepositAmountSnapshot | numeric(12,2)? | Snapshot at add-time |
| AddedAt | Instant | |
| AddedByUserId | Guid | FK only — no nav |

**Indexes:** `OrderId`, `ProductId`.

### StorePayment

**Table:** `store_payments`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| OrderId | Guid | FK to store_orders, cascade delete |
| AmountEur | numeric(12,2) | Signed — negative = refund |
| Method | StorePaymentMethod (int) | Stripe / BankTransfer / Manual |
| StripePaymentIntentId | string(200)? | Unique when present (filtered unique index) |
| ExternalRef | string(200)? | e.g. Holded treasury entry id |
| ReceivedAt | Instant | |
| RecordedByUserId | Guid? | FK only — no nav |
| Notes | string(1000)? | |

**Indexes:** `OrderId`, unique-filtered `StripePaymentIntentId`.

### StoreInvoice

One per order; written once at issuance.

**Table:** `store_invoices`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| OrderId | Guid | Unique |
| HoldedDocId | string(100) | Unique |
| HoldedDocNumber | string(50) | |
| IssuedAt | Instant | |
| IssuedByUserId | Guid | FK only — no nav |
| RequestPayload | jsonb | Full Holded request body for audit |
| ResponsePayload | jsonb | Full Holded response body for audit |

**Indexes:** unique `OrderId`, unique `HoldedDocId`.

### StoreTreasurySyncState

Singleton cursor row (`Id = 1`).

**Table:** `store_treasury_sync_state`

| Property | Type | Notes |
|----------|------|-------|
| Id | int | Always 1 |
| LastSyncAt | Instant? | Cursor for next poll |
| SyncStatus | StoreTreasurySyncStatus (int) | Idle (0) / Running (1) / Failed (2) |
| LastError | string(2000)? | Last error message |

### StoreOrderState

| Value | Int | Description |
|-------|-----|-------------|
| Open | 0 | Lines, counterparty, payments freely editable |
| InvoiceIssued | 1 | Lines + counterparty frozen; payments continue |

Stored as int via `HasConversion<int>()`.

### StorePaymentMethod

| Value | Int | Description |
|-------|-----|-------------|
| Stripe | 0 | From the Stripe webhook |
| BankTransfer | 1 | From the Holded treasury sync job |
| Manual | 2 | Manual entry by FinanceAdmin |

Stored as int via `HasConversion<int>()`.

## Routing

- `/Store` — Camp Lead order browse + create + line edit.
- `/Store/Order/{id}` — Camp Lead order detail (balance + Pay button).
- `/Store/Admin/Catalog` — StoreAdmin catalog CRUD.
- `/Store/Admin/Orders` — FinanceAdmin order ledger + payment entry + Issue Invoice.
- `/Store/Summary` — FinanceAdmin per-camp + per-item summary.
- `/Store/StripeWebhook` — anonymous endpoint for Stripe checkout-session events.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Camp Lead | View / create orders for camp-seasons they lead. Add and remove lines while order is Open and the product's `OrderableUntil` has not passed. Edit counterparty fields while Open. Initiate Stripe checkout to pay. |
| StoreAdmin | **Store-domain superset** (per `memory/code/admin-role-superset.md`): catalog CRUD, view all orders, record manual payments, issue invoices, run treasury sync. Equivalent to FinanceAdmin within the Store section. |
| FinanceAdmin, Admin | All Camp Lead and StoreAdmin capabilities. Record manual payments (incl. refunds via negative amounts) regardless of order state. Issue invoice (single + Issue All). View `/Store/Summary`. Run treasury sync on demand. |

## Invariants

- An order follows the lifecycle: **Open → InvoiceIssued**. There is no return-to-Open transition.
- Lines may only be added or removed while the order is `Open` AND `today <= Product.OrderableUntil` (enforced in `StoreService.AddLineAsync` / `RemoveLineAsync`).
- Counterparty fields (Name, VAT id, Address, CountryCode, Email) are editable only while the order is `Open` (Camp Lead) or by FinanceAdmin/Admin always.
- Line snapshots (`UnitPriceSnapshot`, `VatRateSnapshot`, `DepositAmountSnapshot`) are written at add-time and never recomputed — later catalog edits to `StoreProduct` do not propagate to existing lines.
- Payments may be recorded regardless of order state — payments do not freeze on issuance.
- Issuing an invoice is idempotent: re-issuing an order that already has `IssuedInvoiceId` set throws and does NOT call Holded.
- Issue-invoice failure mid-flight leaves the order in `Open` state with no `StoreInvoice` row (atomic on success only).
- A Stripe `checkout.session.completed` event with a known `humans_store_order_id` inserts at most one `StorePayment` per `StripePaymentIntentId` (filtered unique index + service-level dedup check).
- The treasury sync job matches Holded entries to orders **best-effort** by exact `Order.Label` ↔ entry description; ambiguous matches (multiple orders share a Label) are skipped and logged.
- Resource-based authorization per design-rules §11: `StoreOrderAuthorizationHandler` + `StoreOrderOperationRequirement` gate Camp Lead writes against the order's parent camp-season (Phase 2).

## Negative Access Rules

- A Camp Lead **cannot** add or remove lines after an order's first product `OrderableUntil` has passed (deadline is per-product, evaluated at write time).
- A Camp Lead **cannot** edit lines or counterparty on an order in `InvoiceIssued` state.
- A Camp Lead **cannot** view or edit orders for camp-seasons they do not lead (resource-based auth).
- Anyone other than StoreAdmin/FinanceAdmin/Admin **cannot** issue an invoice or run the treasury sync job manually.
- Re-issuing an already-issued order **cannot** succeed — the second call throws and does not contact Holded.

## Triggers

- Every Order create, line add/remove, counterparty edit, payment record, and invoice issuance emits an audit log entry via `IAuditLogService` (Phases 2, 3, 5).
- `IssueInvoiceAsync` calls `IHoldedClient.UpsertContactAsync` then `IHoldedClient.CreateInvoiceAsync`, then writes the `StoreInvoice` row and flips `StoreOrder.State = InvoiceIssued` in the same logical operation (Phase 5).
- The Stripe webhook controller verifies the request signature with `STRIPE_WEBHOOK_SECRET` and inserts a `StorePayment(Method=Stripe)` for `checkout.session.completed` events (Phase 6).
- `StoreTreasurySyncJob` (Hangfire recurring) polls `IHoldedClient.ListTreasuryEntriesAsync` from `StoreTreasurySyncState.LastSyncAt`, inserts `StorePayment(Method=BankTransfer)` for unambiguous Label matches, and advances the cursor (Phase 7).

## Cross-Section Dependencies

- **Camps:** `ICampService` for `CampSeason` lookups (camp name, lead resolution for resource-based auth).
- **Shifts:** `IShiftManagementService.GetActiveAsync()` for the active event's `Year` and `TimeZoneId` — used to (a) resolve the active catalog year on `/Store` and `/Store/Admin/Catalog` and (b) compute "today in event time zone" for the `OrderableUntil` deadline gate.
- **Auth/Roles:** `RoleNames.StoreAdmin` (this section), `RoleNames.FinanceAdmin`, `RoleNames.Admin`.
- **Holded connector** (Infrastructure): `IHoldedClient` extended with `UpsertContactAsync`, `CreateInvoiceAsync`, `ListTreasuryEntriesAsync` in Phase 4.
- **Stripe connector** (Infrastructure): `IStripeService.CreateCheckoutSessionAsync` for camp-lead payments; `StoreStripeWebhookController` for `checkout.session.completed` ingestion.
- **Audit Log:** `IAuditLogService` for every mutation.

## Stripe Configuration

**One key per Stripe account / purpose.** Each key holds the minimum scope its job requires; production keys are Restricted API Keys (`rk_live_*`).

| Env var | Account | Scope | Set in |
|---|---|---|---|
| `STRIPE_TICKETS_KEY` | Tickets | PaymentIntent + BalanceTransaction reads | dev / QA / prod |
| `STRIPE_STORE_KEY` | Store | `checkout_session:write` only | dev / QA / prod |
| `STRIPE_STORE_WEBHOOK_SECRET` | Store | Webhook signing secret (`whsec_*`) | QA / prod (manual); ephemeral (auto-set at boot) |
| `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` | Store | `webhook_endpoint:read` + `webhook_endpoint:write` | **PR-preview / ephemeral envs only** |
| `Stripe:WebhookCleanupOwner` (config) | — | n/a | PR-preview only — GitHub owner of the fork that produces previews (e.g. `peterdrier`) |
| `Stripe:WebhookCleanupRepository` (config) | — | n/a | PR-preview only — repository name (e.g. `Humans`) |

`STRIPE_API_KEY` is honored as a deprecated alias for `STRIPE_TICKETS_KEY` and emits a one-shot startup warning.

The Store key explicitly does NOT carry refund, payout, charge-modify, customer-write, or PaymentIntent-write scopes. **Refunds, payouts, and chargebacks remain manual via the Stripe dashboard** by policy — bookkeeping for refunds posts to Store as negative `StorePayment` rows via FinanceAdmin manual entry (Phase 5.3).

### Webhook auto-registration (ephemeral envs)

PR-preview environments cannot reasonably create dashboard webhooks per-PR. `StoreWebhookRegistrationService` runs at boot iff `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY` is set:

1. **Cross-PR sweep.** Hits the GitHub API (`GET /repos/{owner}/{repo}/pulls?state=open`) using the existing `GitHub:AccessToken`, builds a set of currently-open PR numbers. Then lists Stripe webhooks owned by this account, filters to URLs matching `{N}.n.burn.camp/Store/StripeWebhook`, parses `{N}` from each host, and deletes any whose PR is no longer open. Idempotent — concurrent boots race harmlessly; 404s on already-deleted endpoints are swallowed.
2. **Current-PR cleanup.** Deletes any webhook whose URL exactly matches this env's URL (handles the redeploy/restart case).
3. **Register.** Creates a fresh endpoint subscribed to all four `checkout.session.*` events (`completed`, `async_payment_succeeded`, `async_payment_failed`, `expired`) so PR-preview matches QA/prod's manual subscription. Today the controller acts only on `completed`; the other three log at Warning until the async-payment state machine ships. Stamps the returned `whsec_*` onto the in-memory `StripeSettings.StoreWebhookSecret` for the process lifetime — Stripe only returns the secret at creation, never via fetch.

The registrar key is **deliberately separate** from `STRIPE_STORE_KEY` so PR-preview testing exercises the production-narrow `checkout_session:write`-only Store key — expanding `STRIPE_STORE_KEY`'s scope in dev would mask scope-related production failures.

**PR-preview Coolify config (the only env that should set these):**
- `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY=rk_test_...` (with `webhook_endpoint:read` + `webhook_endpoint:write`)
- `Stripe:WebhookCleanupOwner=peterdrier`, `Stripe:WebhookCleanupRepository=Humans`

QA and production deliberately do NOT set the registrar key and use a dashboard-configured webhook with a stable signing secret.

The cross-PR sweep means closed-PR cleanup is **self-contained** — no extension to the PR-close GitHub Action is required. The next boot of any PR-preview env (or the same PR redeploying) reaps stale endpoints across the whole account.

### Smoke probe

Boot-time `StripeStartupSmokeService` makes one low-risk read against each configured key (Tickets: PaymentIntents.list; Store: Checkout.Sessions.list). Logs warnings on missing scopes. Stripe does not expose programmatic introspection of RAK scopes, so the probe is positive-confirmation only — it cannot detect over-granted permissions.

## Architecture

**Owning services:** `StoreService`
**Owned tables:** `store_products`, `store_orders`, `store_order_lines`, `store_payments`, `store_invoices`, `store_treasury_sync_state`
**Status:** (A) Migrated — new section, born §15-compliant (peterdrier/Humans store-foundation, 2026-04-30).

- `StoreService` lives in `Humans.Application.Services.Store` and depends only on Application-layer abstractions.
- `StoreRepository` (impl `Humans.Infrastructure/Repositories/Store/StoreRepository.cs`, §15b Singleton + `IDbContextFactory`) is the only file that touches Store tables via `DbContext`.
- **Decorator decision — no caching decorator.** Store is admin / camp-lead only, low-traffic; same rationale as Budget / Governance.
- **Cross-domain navs:** none. `CampSeasonId`, `ProductId`, `AddedByUserId`, `RecordedByUserId`, `IssuedByUserId` are all FK-only with no navigation property.
- **Cross-section calls** route through `ICampService` (camp / camp-season lookups), `IShiftManagementService` (active event year + time-zone), `IAuditLogService`, `IHoldedClient`, `IStripeService`.

Phase 1 ships entities, migration, role, service skeleton (methods throw `NotSupportedException` until later phases). Phases 2–7 progressively fill the surface; see `docs/superpowers/specs/2026-04-30-store-section-design.md` and `docs/superpowers/plans/2026-04-30-store-section.md`.
