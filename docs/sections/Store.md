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

- A **Store Product** is a catalog item available to Camp Leads and department coordinators in a given event year (price, VAT rate, optional deposit, ordering deadline). Products are created and edited by StoreAdmin.
- A **Store Order** is owned by exactly one counterparty — either a `CampSeason` (billable, full lifecycle Open → InvoiceIssued, multiple orders per season allowed) **or** a `Team` (non-billable, department-level only, stays `Open` indefinitely, one order per team per year). The "exactly one" invariant is service-enforced, not DB-enforced. Both kinds reuse the same `StoreProduct` catalog and the `OrderableUntil` deadline gate.
- A **Store Order Line** is a line on an order that snapshots the product's price, VAT, and deposit at the time the line was added — later catalog edits never mutate existing lines.
- A **Store Payment** is a payment against a camp order, recorded with one of three methods (`Stripe`, `BankTransfer`, `Manual`) and a `Status` (`Paid` / `Pending` / `Failed`) reflecting what Stripe has confirmed about the money — a captured debit mandate is `Pending`, not `Paid`. Only `Paid` rows count toward the order balance. Negative amounts represent refunds. Team orders never have payments.
- A **Store Invoice** is the consolidated Holded factura issued for a camp order. One invoice per order, written once at issuance. Team orders never receive invoices.
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
| CampSeasonId | Guid? | FK only — no nav. Set for camp orders; null for team orders. |
| TeamId | Guid? | FK only — no nav. Set for team orders; null for camp orders. |
| Year | int | Event year the catalog draws from. Always set on write; lazy-backfilled from `CampSeason.Year` for legacy camp rows. |
| Label | string(100)? | `[Obsolete]` — removed from the UI (#816); column retained but unused, never set on write |
| State | StoreOrderState (int) | Open or InvoiceIssued; team orders stay Open |
| CounterpartyName / CounterpartyVatId / CounterpartyAddress / CounterpartyCountryCode / CounterpartyEmail | string? | Editable by Camp Lead while Open; FinanceAdmin always. Never populated on team orders. |
| IssuedInvoiceId | Guid? | Set when invoice is issued (camp orders only) |
| CreatedAt / UpdatedAt | Instant | |

**Indexes:** `CampSeasonId`, `TeamId`, `State`.

**Cross-section linkage:** `CampSeasonId` and `TeamId` are bare `Guid?` columns — no FK constraint, no navigation property (per `memory/architecture/no-cross-section-ef-joins.md`). Resolved at the service layer via `ICampServiceRead.GetCampSeasonByIdAsync` / `ITeamServiceRead.GetTeamAsync`.

**Year backfill rule:** new writes always populate `Year`. Pre-existing camp rows may carry `Year = 0` until they're next saved through the service, at which point the column is backfilled from `CampSeason.Year`.

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

**Indexes:** `OrderId`. `ProductId` — intra-section FK to `store_products` (`OnDelete=Restrict`, no navigation property).

### StorePayment

**Table:** `store_payments`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| OrderId | Guid | FK to store_orders, cascade delete |
| AmountEur | numeric(12,2) | Signed — negative = refund |
| Method | StorePaymentMethod (int) | Stripe / BankTransfer / Manual |
| Status | StorePaymentStatus (string) | Paid / Pending / Failed. Defaults to Paid (column default + entity default). Only Paid counts toward balance. |
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

**Constraints:** `OrderId` — intra-section FK to `store_orders` (one-to-one, `OnDelete=Restrict`); unique implicit. `HoldedDocId` — unique index.

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

### StorePaymentStatus

| Value | Description |
|-------|-------------|
| Paid | Stripe confirmed settlement (sync at `completed`; async at `async_payment_succeeded`). Counts toward the order balance. |
| Pending | Async mandate captured, not yet cleared (SEPA, delayed Bizum). Excluded from the balance until settlement confirms. |
| Failed | Mandate rejected or settlement bounced (`async_payment_failed`). Treated as zero. |

Stored as **string** via `HasConversion<string>()` with column default `Paid`. `Paid` is deliberately the zero/default enum member: it is the value for every pre-async-support row and for sync/manual inserts, and being EF's insert sentinel it prevents an explicitly-set `Pending` from being swallowed by the store default (the enum analogue of the bool-sentinel trap).

## Routing

- `/Store` — Camp Lead and department-coordinator order browse + create + line edit. Each counterparty (camp-you-lead or department-you-coordinate) is rendered as its own card. A privileged reader (StoreAdmin/FinanceAdmin/Admin **or** TeamsAdmin) sees every camp season and department for the year, not just the ones they lead/coordinate; per-row Create/Delete affordances are resolved against `StoreOrderAuthorizationHandler` rather than a blanket admin flag.
- `/Store/Order/{id}` — Order detail. Lines display the **effective** unit price (live catalog price for an Open order, frozen snapshot once InvoiceIssued — #816). Camp orders show summary cards (lines subtotal, VAT, deposits, total cleared payments, balance owed — the balance and total exclude `Pending`/`Failed`), a "price changes since this order started" table (from `StoreProductPriceChanged` audit entries), the recorded-payments list (date, method, **status** Paid/Pending/Failed, Stripe/external reference, amount) with a "€X pending settlement" banner when async mandates are uncleared, the Pay button, and a collapsed-by-default counterparty section. Team orders show only lines + add-line form + a "non-billable" footer (no counterparty form, no Pay, no payments list).
- `/Store/Team/{teamId}/Create` — POST: department coordinator creates their team's order for the active event year.
- `/Store/Admin/Catalog` — StoreAdmin catalog CRUD (`StoreAdminController`, policy `StoreCatalogAdmin`).
- `/Store/Admin/Catalog/Edit[/{id}]` — Create / edit product.
- `/Store/Admin/Catalog/Save` — POST save product.
- `/Store/Admin/Catalog/Deactivate/{id}` — POST soft-deactivate product.
- `/Store/Admin/Orders` — FinanceAdmin order ledger + payment entry + Issue Invoice. **Not yet implemented (Phase 5 stub).**
- `/Store/Admin/Summary` — FinanceAdmin/StoreAdmin/Admin aggregate report: by-counterparty (with Type column distinguishing Camp / Team), by-item (sums lines from both camp and team orders for supplier aggregation), counterparties × products cross-tab for a given year. **Totals use effective pricing** — Open orders are repriced to the live catalog (matching the order-page behavior), InvoiceIssued orders use their frozen snapshots. Reuses `PolicyNames.StoreCatalogAdmin`.
- `/Store/Admin/Payments` — FinanceAdmin/StoreAdmin/Admin Stripe payment reconciliation screen: webhook/checkout health banner, every Store Checkout Session matched to its order with a status (Recorded / Missing / Unmatched / Unpaid), and orphan recorded payments. Reuses `PolicyNames.StoreCatalogAdmin`. Linked from the Store-admin button group on `/Store` and the admin sidebar (**Store → Store payments**).
- `/Store/Admin/Payments/RecordMissing` — POST: records every paid, order-matched, not-yet-recorded session via the idempotent `RecordStripePaymentAsync` path.
- `/Store/StripeWebhook` — anonymous endpoint for Stripe checkout-session events (`StoreStripeWebhookController`).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Camp Lead | View / create orders for camp-seasons they lead. Add and remove lines while order is Open and the product's `OrderableUntil` has not passed. Edit counterparty fields while Open. Initiate Stripe checkout to pay. |
| Coordinator (department) | View / create the single team order for departments (top-level teams) they coordinate, scoped to the active event year. Add and remove lines while the product's `OrderableUntil` has not passed. No pay, no counterparty edit, no invoice — team orders are non-billable. |
| StoreAdmin | **Store-domain superset** (per `memory/code/admin-role-superset.md`): catalog CRUD, view all orders, record manual payments, issue invoices, run treasury sync, reconcile Stripe payments (`/Store/Admin/Payments`). Equivalent to FinanceAdmin within the Store section. EditCounterparty/Pay remain denied on team orders even for admins. |
| TeamsAdmin | **View any order** (camp or team) and **manage team orders only** (AddLine / RemoveLine while `Open`; Delete any state). Camp orders are view-only. Never Pay / EditCounterparty (team orders are non-billable). Additive — a TeamsAdmin who is also a camp lead keeps camp-edit rights through the lead path. |
| FinanceAdmin, Admin | All Camp Lead and StoreAdmin capabilities. Record manual payments (incl. refunds via negative amounts) regardless of order state. Issue invoice (single + Issue All). View `/Store/Admin/Summary` and `/Store/Admin/Payments`. Reconcile missing Stripe payments. Run treasury sync on demand. EditCounterparty/Pay remain denied on team orders. |

## Invariants

- An order has **exactly one counterparty** — `CampSeasonId` xor `TeamId` is non-null. The invariant is service-enforced (in `StoreService.CreateOrderAsync` / `CreateTeamOrderAsync`), not DB-enforced.
- **Team orders are non-billable.** `UpdateCounterpartyAsync`, `RecordManualPaymentAsync`, `RecordStripePaymentAsync`, `CreateStripeCheckoutSessionAsync`, and `IssueInvoiceAsync` reject any order whose `TeamId is not null` with `InvalidOperationException`. The auth handler also permanently denies the `EditCounterparty` and `Pay` operations on team orders regardless of role.
- A team order is restricted to a **department** (top-level team — `ParentTeamId is null`). Sub-team orders are not supported.
- At most **one team order per team per year** — enforced by `CreateTeamOrderAsync` via a repo lookup before insert.
- Camp orders follow the lifecycle: **Open → InvoiceIssued**. There is no return-to-Open transition.
- Team orders stay in **Open** indefinitely. The implicit close-out signal is per-product `OrderableUntil` — once every catalog product has passed its deadline, the order is effectively read-only.
- Lines may only be added or removed while the order is `Open` AND `today <= Product.OrderableUntil`. The deadline gate is per-product and identical for camp and team orders. It is enforced at the **authorization layer** (`StoreOrderAuthorizationHandler`, using the `StoreOrderLineContext` resource): non-admins are denied past the deadline; Store admins are exempt and may edit lines on any Open order regardless of `OrderableUntil`. `StoreService.AddLineAsync` / `RemoveLineAsync` no longer throw on a passed deadline — they only annotate the audit entry with `(past order deadline …)` when the line is edited past it.
- Counterparty fields (`CounterpartyName`, `CounterpartyVatId`, `CounterpartyAddress`, `CounterpartyCountryCode`, `CounterpartyEmail`) are editable only while the order is `Open` (Camp Lead) or by FinanceAdmin/Admin always.
- Line snapshots (`UnitPriceSnapshot`, `VatRateSnapshot`, `DepositAmountSnapshot`) are written at add-time and never recomputed. **Effective pricing differs by order state (#816):** an `Open` order is a live running tab — `BalanceCalculator.Compute` reprices its lines to the *current* catalog price for the event year (falling back to the snapshot when the product is absent from the catalog), so catalog edits DO propagate to Open orders. An `InvoiceIssued` order is frozen and always reads each line's add-time snapshot.
- Payments may be recorded regardless of order state — payments do not freeze on issuance.
- Issuing an invoice is idempotent: re-issuing an order that already has `IssuedInvoiceId` set throws and does NOT call Holded.
- Issue-invoice failure mid-flight leaves the order in `Open` state with no `StoreInvoice` row (atomic on success only).
- A Stripe `checkout.session.completed` event with a known `humans_store_order_id` inserts at most one `StorePayment` per `StripePaymentIntentId` (filtered unique index + service-level dedup check). The inserted row's `Status` is **`Paid`** when `session.payment_status == "paid"` (sync card/wallet) and **`Pending`** otherwise (`"unpaid"` — async mandate captured but not yet cleared, e.g. SEPA).
- **Balance counts `Paid` only.** `BalanceCalculator.Compute` sums payments where `Status == Paid`; `Pending` and `Failed` rows are excluded, so a captured-but-uncleared mandate never makes an order look paid.
- **Async-payment state machine** (`HandleStripeCheckoutWebhookEventAsync`, all idempotent):
  - `checkout.session.async_payment_succeeded` → the matching `Pending` row transitions to `Paid` (`StorePaymentSettled` audit). Re-delivery of an already-`Paid` row is a no-op. Out-of-order delivery (succeeded before `completed`, so no row yet) records a `Paid` payment directly so settled money is never lost.
  - `checkout.session.async_payment_failed` → the matching `Pending` row transitions to `Failed` (`StorePaymentFailed` audit), leaving the order unpaid — never paid-then-reversed. A failure with no matching row is a no-op (no money was ever pending).
  - `checkout.session.expired` → defensively deletes an orphan `Pending` row for the session's PI (`StorePaymentExpired` audit); a `Paid` or `Failed` row is never touched.
- **Reconciliation is the recovery path when the webhook misses a payment** (e.g. `STRIPE_STORE_WEBHOOK_SECRET` unset → webhook 503s). `RecordMissingStripePaymentsAsync` lists Store Checkout Sessions, and records only those that are `payment_status == paid`, resolve to an existing **billable** (non-Team) order via `humans_store_order_id` metadata, and are not already recorded. Amount + PaymentIntent id come **from Stripe** — never fabricated. Idempotent (same PI-id guard as the webhook), so it is safe to re-run. Unmatched sessions and orphan recorded payments are surfaced read-only, never auto-recorded or auto-deleted.
- The treasury sync job (Phase 7, not yet implemented) will match Holded entries to orders **best-effort**; the original `Order.Label` matching key was removed with the Label field (#816), so the eventual matching strategy is TBD.
- Resource-based authorization per design-rules §11: `StoreOrderAuthorizationHandler` + `StoreOrderOperationRequirement` gate Camp Lead writes against the order's parent camp-season. Operations: `View`, `Create`, `AddLine`, `RemoveLine`, `EditCounterparty`, `Pay`, `Delete`. Mutating ops (`AddLine`, `RemoveLine`, `EditCounterparty`) are gated on `State = Open`; `View` and `Pay` carry no state gate. `Delete` is admin-only (Admin/FinanceAdmin/StoreAdmin on any order; TeamsAdmin on team orders) — camp leads and team coordinators never delete their own orders. A **TeamsAdmin** additionally passes `View` on any order and manages team orders only — `AddLine`/`RemoveLine` (Open only); camp orders stay view-only for them, and `EditCounterparty`/`Pay` are never granted on team orders. The **product deadline gate** is also enforced here: when the authorization resource is a `StoreOrderLineContext` (carrying the product's `OrderableUntil`), non-admin line edits (`AddLine` / `RemoveLine`) are denied once today's event-zone date is past the deadline; Store admins are exempt.

## Negative Access Rules

- A Camp Lead **cannot** add or remove lines after the product's `OrderableUntil` has passed (deadline is per-product, enforced by `StoreOrderAuthorizationHandler` at request time; Store admins are exempt).
- A Camp Lead **cannot** edit lines or counterparty on an order in `InvoiceIssued` state.
- A Camp Lead **cannot** view or edit orders for camp-seasons they do not lead (resource-based auth).
- Anyone other than StoreAdmin/FinanceAdmin/Admin **cannot** issue an invoice or run the treasury sync job manually.
- Re-issuing an already-issued order **cannot** succeed — the second call throws and does not contact Holded.

## Triggers

**Live:**
- Order create, line add/remove, counterparty edit, and Stripe payment record emit audit log entries via `IAuditLogService` (`StoreOrderCreated`, `StoreLineAdded`, `StoreLineRemoved`, `StoreCounterpartyEdited`, `StorePaymentRecorded`). Async-payment transitions emit `StorePaymentSettled` (Pending → Paid), `StorePaymentFailed` (Pending → Failed), and `StorePaymentExpired` (orphan Pending removed on session expiry), all with the `StripeWebhook` job actor.
- Product create, update, and deactivate emit `StoreProductCreated`, `StoreProductUpdated`, `StoreProductDeactivated`. A product update that changes the unit price additionally emits a dedicated, queryable `StoreProductPriceChanged` entry (#816); the order page surfaces these for an order's products since it was created, and the catalog edit page shows per-product price history.
- The Stripe webhook controller (`StoreStripeWebhookController`) verifies the request signature via `IStripeService.ParseStoreCheckoutEvent` and dispatches to `IStoreService.HandleStripeCheckoutWebhookEventAsync`, which handles all four `checkout.session.*` events (completed + the async-payment state machine above). Idempotent on `StripePaymentIntentId`.
- `/Store/Admin/Payments/RecordMissing` reconciles Stripe → ledger on demand (admin-triggered), recording missing paid sessions via the same idempotent path and emitting one `StorePaymentsReconciled` summary audit entry (with the human actor) plus the per-payment `StorePaymentRecorded` entries. The webhook is therefore no longer the *sole* writer of Stripe payments — but it remains the only automatic one.

**Not yet shipped (Phase 5+):**
- `IssueInvoiceAsync` — will call `IHoldedClient.UpsertContactAsync` then `IHoldedClient.CreateInvoiceAsync`, write the `StoreInvoice` row, flip `StoreOrder.State = InvoiceIssued`, and emit `StorePaymentRecorded` audit entry. Currently throws `NotSupportedException("Phase 5")`.
- `RecordManualPaymentAsync` — manual payment entry by FinanceAdmin. Currently throws `NotSupportedException("Phase 5")`.
- `StoreTreasurySyncJob` (Hangfire recurring) — polls `IHoldedClient.ListTreasuryEntriesAsync` from `StoreTreasurySyncState.LastSyncAt`, inserts `StorePayment(Method=BankTransfer)` for unambiguous matches, advances cursor. Not yet implemented (the original Label matching key was removed in #816).

## Cross-Section Dependencies

- **Camps:** `ICampServiceRead` for `CampSeason` lookups (camp name, lead resolution for resource-based auth).
- **Teams:** `ITeamServiceRead` for department lookups (team name, department check via `ParentTeamId is null`, coordinator check via `ManagementRoleHolderUserIds`). Existing methods only — no new surface added to its `[SurfaceBudget(4)]`.
- **Shifts:** `IShiftManagementService.GetActiveAsync()` for the active event's `Year` and `TimeZoneId` — used to (a) resolve the active catalog year on `/Store` and `/Store/Admin/Catalog`, (b) populate `Year` on new team orders, and (c) compute "today in event time zone" for the `OrderableUntil` deadline gate.
- **Auth/Roles:** `RoleNames.StoreAdmin` (this section), `RoleNames.FinanceAdmin`, `RoleNames.Admin`.
- **Holded connector** (Infrastructure): `IHoldedClient` extended with `UpsertContactAsync`, `CreateInvoiceAsync`, `ListTreasuryEntriesAsync` in Phase 4.
- **Stripe connector** (Infrastructure): `IStripeService.CreateCheckoutSessionAsync` for camp-lead payments; `StoreStripeWebhookController` for `checkout.session.completed` ingestion.
- **Audit Log:** `IAuditLogService` for every mutation.

## Stripe Connector

The Store section uses `IStripeService` (Application-layer abstraction; Infrastructure impl in `Humans.Infrastructure/Services/StripeService.cs`).

- `STRIPE_STORE_KEY` — `checkout_session:write` (Write ⊇ Read, so it also creates Checkout Sessions **and** lists/reads them for reconciliation via `ListStoreCheckoutSessionsAsync`). Each session is created with `humans_store_order_id` stamped on **both** the session metadata and the PaymentIntent metadata, plus a legible description, so payments are matchable from the dashboard, receipts, and PI search. Refunds, payouts, and chargebacks remain manual via the Stripe dashboard; the bookkeeping side posts as negative `StorePayment` rows via FinanceAdmin manual entry (Phase 5.3).
- `STRIPE_STORE_WEBHOOK_SECRET` — signing secret for `StoreStripeWebhookController`. Set manually in QA/prod; auto-provisioned at boot in PR-preview envs via `StoreWebhookRegistrationService` (requires `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY`).
- Webhook events subscribed and handled: `checkout.session.completed` (records Paid or Pending by `payment_status`), `checkout.session.async_payment_succeeded` (Pending → Paid), `checkout.session.async_payment_failed` (Pending → Failed), `checkout.session.expired` (orphan-Pending cleanup) — the async-payment state machine (nobodies-collective/Humans#638).
- Boot-time `StripeStartupSmokeService` validates each key with one low-risk read (Checkout.Sessions.list for Store key). Positive-confirmation only — cannot detect over-granted scopes.

## Architecture

**Owning services:** `StoreService`
**Owned tables:** `store_products`, `store_orders`, `store_order_lines`, `store_payments`, `store_invoices`, `store_treasury_sync_state`
**Status:** (A) Migrated — new section, born §15-compliant (peterdrier/Humans store-foundation, 2026-04-30).

- `StoreService` lives in `Humans.Application.Services.Store` and depends only on Application-layer abstractions.
- `IStoreRepository` interface lives in `Humans.Application.Interfaces.Repositories`. `StoreRepository` (impl `Humans.Infrastructure/Repositories/Store/StoreRepository.cs`, §15b Singleton + `IDbContextFactory`) is the only file that touches Store tables via `DbContext`.
- **Decorator decision — no caching decorator.** Store is admin / camp-lead only, low-traffic; same rationale as Budget / Governance.
- **Cross-domain navs:** none. `CampSeasonId`, `ProductId`, `AddedByUserId`, `RecordedByUserId`, `IssuedByUserId` are all FK-only with no navigation property. Intra-section back-navs `StoreOrderLine.Order` and `StorePayment.Order` are aggregate-local and are kept.
- **Cross-section calls** route through `ICampServiceRead` (camp / camp-season lookups), `IShiftManagementService` (active event year + time-zone), `IAuditLogService`, `IHoldedClient`, `IStripeService`.
- **Architecture test:** none yet. `tests/Humans.Application.Tests/Architecture/StoreArchitectureTests.cs` is not present — gap to fill in a follow-up.

Implementation status: catalog CRUD (create, update, deactivate), order create, add/remove line, counterparty edit, and Stripe payment recording are live. `RecordManualPaymentAsync`, `IssueInvoiceAsync`, treasury sync, and the Orders admin view throw `NotSupportedException("Phase 5")`. See `docs/superpowers/specs/2026-04-30-store-section-design.md`.
