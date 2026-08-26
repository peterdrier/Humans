<!-- freshness:triggers
  src/Sections/Humans.Store/**
  src/Sections/Humans.Stripe/**
-->
<!-- freshness:flag-on-change
  Store catalog editing, order lifecycle, OrderableUntil gate, invoice issuance idempotency, treasury sync matching, Stripe Checkout / webhook signature verification, and resource-based authorization — review when Store services/entities/controllers/auth handlers/Stripe surfaces change.
-->

# Store — Section Invariants

Per-camp catalog ordering, multi-method payments, and consolidated Holded factura issuance for Camp Lead purchases.

## Concepts

- A **Store Product** is a catalog item available to Camp Leads and department coordinators in a given event year (price, VAT rate, optional deposit, ordering deadline). Products are created and edited by StoreAdmin.
- A **Store Order** is owned by exactly one counterparty — either a `CampSeason` (billable, full lifecycle Open → InvoiceIssued, at most one order per camp season — and a camp season is itself one camp's one year) **or** a `Team` (non-billable, department-level only, stays `Open` indefinitely, one order per team per year). The "exactly one" invariant is service-enforced, not DB-enforced. Both kinds reuse the same `Product` catalog and the `OrderableUntil` deadline gate.
- A **Store Order Line** is a line on an order that snapshots the product's price, VAT, and deposit at the time the line was added — later catalog edits never mutate existing lines.
- A **Store Payment** is a payment against a camp order, recorded with one of three methods (`Stripe`, `BankTransfer`, `Manual`) and a `Status` (`Paid` / `Pending` / `Failed`) reflecting what Stripe has confirmed about the money — a captured debit mandate is `Pending`, not `Paid`. Only `Paid` rows count toward the order balance. Negative amounts represent refunds. Team orders never have payments.
- A **Store Invoice** is the consolidated Holded factura issued for a camp order. One invoice per order, written once at issuance. Team orders never receive invoices.
- A **Store Treasury Sync State** is the singleton cursor row a treasury-sync job would use to track its last successful Holded poll. The table ships; no such job exists and no code reads or writes the row.

## Data Model

### Product

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
| HoldedRevenueAccountNum | int? | Holded chart number this item's external revenue books to (`75900001` Bus Tickets, `75900002` ice, …). Set by StoreAdmin from Acountax's numbering; null until issued. The internal-recharge twin (`75910002`) is **derived** (`ProductDto.InternalRechargeAccountNum` = number + 10 000), never stored. |
| OrderableUntil | LocalDate | Add-line deadline |
| IsActive | bool | Soft-deactivate |
| CreatedAt | Instant | |
| UpdatedAt | Instant | |

**Indexes:** `(Year, IsActive)`.

### Order

A camp's order against a season.

**Table:** `store_orders`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| CampSeasonId | Guid? | FK only — no nav. Set for camp orders; null for team orders. |
| TeamId | Guid? | FK only — no nav. Set for team orders; null for camp orders. |
| Year | int | Event year the catalog draws from. Always set on write; lazy-backfilled from `CampSeason.Year` for legacy camp rows. |
| Label | string(100)? | `[Obsolete]` — removed from the UI (#816) and from every DTO and code path since; the column ships, nothing reads or writes it |
| State | OrderState (int) | Open or InvoiceIssued; team orders stay Open |
| CounterpartyName / CounterpartyVatId / CounterpartyAddress / CounterpartyCountryCode / CounterpartyEmail | string? | Editable by Camp Lead while Open; FinanceAdmin always. Never populated on team orders. |
| IssuedInvoiceId | Guid? | Set when invoice is issued (camp orders only) |
| CreatedAt / UpdatedAt | Instant | |

**Indexes:** `CampSeasonId`, `TeamId`, `State`.

**Cross-section linkage:** `CampSeasonId` and `TeamId` are bare `Guid?` columns — no FK constraint, no navigation property (per `memory/architecture/no-cross-section-ef-joins.md`). Resolved at the service layer via `ICampServiceRead.GetCampSeasonByIdAsync` / `ITeamServiceRead.GetTeamAsync`.

**Year backfill rule:** new writes always populate `Year`. Pre-existing camp rows may carry `Year = 0` until they're next saved through the service, at which point the column is backfilled from `CampSeason.Year`.

**Aggregate-local navs:** `Order.Lines`, `Order.Payments`.

### OrderLine

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

### Payment

**Table:** `store_payments`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| OrderId | Guid | FK to store_orders, cascade delete |
| AmountEur | numeric(12,2) | Signed — negative = refund |
| Method | PaymentMethod (int) | Stripe / BankTransfer / Manual |
| Status | PaymentStatus (string) | Paid / Pending / Failed. Defaults to Paid (entity initializer; the column carries no default). Only Paid counts toward balance. |
| StripePaymentIntentId | string(200)? | Unique when present (filtered unique index) |
| ExternalRef | string(200)? | e.g. Holded treasury entry id |
| ReceivedAt | Instant | |
| RecordedByUserId | Guid? | FK only — no nav |
| Notes | string(1000)? | |

**Indexes:** `OrderId`, unique-filtered `StripePaymentIntentId`.

### Invoice

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

### TreasurySyncState

Singleton cursor row (`Id = 1`).

**Table:** `store_treasury_sync_state`

| Property | Type | Notes |
|----------|------|-------|
| Id | int | Always 1 |
| LastSyncAt | Instant? | Cursor for next poll |
| SyncStatus | TreasurySyncStatus (int) | Idle (0) / Running (1) / Failed (2) |
| LastError | string(2000)? | Last error message |

### OrderState

| Value | Int | Description |
|-------|-----|-------------|
| Open | 0 | Lines, counterparty, payments freely editable |
| InvoiceIssued | 1 | Lines + counterparty frozen; payments continue |

Stored as int via `HasConversion<int>()`.

### PaymentMethod

| Value | Int | Description |
|-------|-----|-------------|
| Stripe | 0 | From the Stripe webhook |
| BankTransfer | 1 | From the Holded treasury sync job |
| Manual | 2 | Manual entry by FinanceAdmin |

Stored as int via `HasConversion<int>()`.

### PaymentStatus

| Value | Description |
|-------|-------------|
| Paid | Stripe confirmed settlement (sync at `completed`; async at `async_payment_succeeded`). Counts toward the order balance. |
| Pending | Async mandate captured, not yet cleared (SEPA, delayed Bizum). Excluded from the balance until settlement confirms. |
| Failed | Mandate rejected or settlement bounced (`async_payment_failed`). Treated as zero. |

Stored as **string** via `HasConversion<string>()`. The column carried a `Paid` default only for the `AddStorePaymentStatus` migration, so pre-async rows landed settled without a data backfill; it was dropped once that migration ran, and `Payment.Status`'s C# initializer covers inserts. `Paid` remains the zero/default enum member deliberately: it is the value every existing row has and the value sync and manual inserts want.

## Routing

- `/Store` — Camp Lead and department-coordinator order browse + create + line edit. Each counterparty (camp-you-lead or department-you-coordinate) is rendered as its own card. A privileged reader (StoreAdmin/FinanceAdmin/Admin **or** TeamsAdmin) sees every camp season and department for the year, not just the ones they lead/coordinate; per-row Create/Delete affordances are resolved against `OrderAuthorizationHandler` rather than a blanket admin flag.
- `/Store/Order/{id}` — Order detail. Lines display the **effective** unit price (live catalog price for an Open order, frozen snapshot once InvoiceIssued — #816). Camp orders show summary cards (lines subtotal, VAT, deposits, total cleared payments, balance owed — the balance and total exclude `Pending`/`Failed`), a "price changes since this order started" table (rendered by `<vc:audit-log layout="table">` over the order's product ids — Store does not read audit), the recorded-payments list (date, method, **status** Paid/Pending/Failed, Stripe/external reference, amount) with a "€X pending settlement" banner when async mandates are uncleared, the Pay button, and a collapsed-by-default counterparty section. Team orders show only lines + add-line form + a "non-billable" footer (no counterparty form, no Pay, no payments list).
- `/Store/Team/{teamId}/Create` — POST: department coordinator creates their team's order for the active event year.
- `/Store/Admin/Catalog` — StoreAdmin catalog CRUD (`StoreAdminController`, policy `StoreCatalogAdmin`).
- `/Store/Admin/Catalog/Edit[/{id}]` — Create / edit product.
- `/Store/Admin/Catalog/Save` — POST save product.
- `/Store/Admin/Catalog/Deactivate/{id}` — POST soft-deactivate product.
- `/Store/Order/{id}/IssueInvoice` — POST: Store admin issues the order's Holded factura. The button lives on the order page, next to Delete, and renders only for an `Open` camp order with at least one line.
- `/Store/Admin/Summary` — FinanceAdmin/StoreAdmin/Admin aggregate report: by-counterparty (with Type column distinguishing Camp / Team), by-item (sums lines from both camp and team orders for supplier aggregation), counterparties × products cross-tab for a given year. **Totals use effective pricing** — Open orders are repriced to the live catalog (matching the order-page behavior), InvoiceIssued orders use their frozen snapshots. Reuses `PolicyNames.StoreCatalogAdmin`.
- `/Store/Admin/Payments` — FinanceAdmin/StoreAdmin/Admin Stripe payment reconciliation screen: webhook/checkout health banner, every Store Checkout Session matched to its order with a status (Recorded / Missing / Unmatched / Unpaid), and orphan recorded payments. Reuses `PolicyNames.StoreCatalogAdmin`. Linked from the Store-admin button group on `/Store` and the admin sidebar (**Store → Store payments**).
- `/Store/Admin/Payments/RecordMissing` — POST: records every paid, order-matched, not-yet-recorded session via the idempotent `RecordStripePaymentAsync` path.
- `/Store/StripeWebhook` — anonymous endpoint for Stripe checkout-session events (`StoreStripeWebhookController`).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Camp Lead | View / create orders for camp-seasons they lead. Add and remove lines while order is Open and the product's `OrderableUntil` has not passed. Edit counterparty fields while Open. Initiate Stripe checkout to pay. |
| Coordinator (department) | View / create the single team order for departments (top-level teams) they coordinate, scoped to the active event year. Add and remove lines while the product's `OrderableUntil` has not passed. No pay, no counterparty edit, no invoice — team orders are non-billable. |
| StoreAdmin | **Store-domain superset** (per `memory/code/admin-role-superset.md`): catalog CRUD, view all orders, issue invoices, reconcile Stripe payments (`/Store/Admin/Payments`). Equivalent to FinanceAdmin within the Store section. EditCounterparty/Pay remain denied on team orders even for admins. |
| TeamsAdmin | **View any order** (camp or team) and **manage team orders only** (Create for any department, not only the ones they coordinate; AddLine / RemoveLine while `Open`; Delete any state). Camp orders are view-only. Never Pay / EditCounterparty (team orders are non-billable). Additive — a TeamsAdmin who is also a camp lead keeps camp-edit rights through the lead path. |
| FinanceAdmin, Admin | All Camp Lead and StoreAdmin capabilities. Issue invoice from the order page. View `/Store/Admin/Summary` and `/Store/Admin/Payments`. Reconcile missing Stripe payments. EditCounterparty/Pay remain denied on team orders. |

## Invariants

- An order has **exactly one counterparty** — `CampSeasonId` xor `TeamId` is non-null. The invariant is service-enforced (in `Service.CreateOrderAsync` / `CreateTeamOrderAsync`), not DB-enforced.
- **Team orders are non-billable.** `UpdateCounterpartyAsync`, `RecordStripePaymentAsync`, `CreateStripeCheckoutSessionAsync`, and `IssueInvoiceAsync` reject any order whose `TeamId is not null` with `InvalidOperationException`. The auth handler also permanently denies the `EditCounterparty` and `Pay` operations on team orders regardless of role.
- A team order is restricted to a **department** (top-level team — `ParentTeamId is null`). Sub-team orders are not supported.
- At most **one team order per team per year** — enforced by `CreateTeamOrderAsync` via a repo lookup before insert.
- Camp orders follow the lifecycle: **Open → InvoiceIssued**. There is no return-to-Open transition.
- Team orders stay in **Open** indefinitely. The implicit close-out signal is per-product `OrderableUntil` — once every catalog product has passed its deadline, the order is effectively read-only.
- Lines may only be added or removed while the order is `Open` AND `today <= Product.OrderableUntil`. The deadline gate is per-product and identical for camp and team orders. It is enforced at the **authorization layer** (`OrderAuthorizationHandler`, using the `OrderLineContext` resource): non-admins are denied past the deadline; Store admins are exempt and may edit lines on any Open order regardless of `OrderableUntil`. `Service.AddLineAsync` / `RemoveLineAsync` no longer throw on a passed deadline — they only annotate the audit entry with `(past order deadline …)` when the line is edited past it.
- Counterparty fields (`CounterpartyName`, `CounterpartyVatId`, `CounterpartyAddress`, `CounterpartyCountryCode`, `CounterpartyEmail`) are editable only while the order is `Open` (Camp Lead) or by FinanceAdmin/Admin always.
- Line snapshots (`UnitPriceSnapshot`, `VatRateSnapshot`, `DepositAmountSnapshot`) are written at add-time and never recomputed. **Effective pricing differs by order state (#816):** an `Open` order is a live running tab — `BalanceCalculator.Compute` reprices its lines to the *current* catalog price for the event year (falling back to the snapshot when the product is absent from the catalog), so catalog edits DO propagate to Open orders. An `InvoiceIssued` order is frozen and always reads each line's add-time snapshot.
- Payments may be recorded regardless of order state — payments do not freeze on issuance.
- **Spanish VAT applies to every order regardless of buyer country** — all goods are physically handed over on-site in Spain, so place of supply is Spain and there is no B2B reverse-charge path. VAT comes solely from the per-product `VatRatePercent` snapshot; `CounterpartyCountryCode` is stored for the factura but never consulted for tax.
- **Deposits are VAT-free** (refundable security deposits / fianzas are not subject to VAT): `BalanceCalculator.Compute` adds deposit amounts without applying VAT, and the future Holded invoice renders each deposit as a separate `tax = 0` line.
- Issuing an invoice is idempotent: re-issuing an order that already has `IssuedInvoiceId` set (or already in `InvoiceIssued`) throws and does NOT call Holded.
- Issue-invoice failure mid-flight leaves the order in `Open` state with no `Invoice` row (atomic on success only — `IStoreRepository.SaveIssuedInvoiceAsync` writes the invoice row and the frozen order in one `SaveChanges`).
- **Issuance freezes the price (#816).** Before flipping to `InvoiceIssued`, each line's `UnitPriceSnapshot` / `VatRateSnapshot` / `DepositAmountSnapshot` is rewritten from the live catalog, so the issued document and the order agree forever after.
- **Every issued document is approved.** A Holded draft books no revenue, so `IssueInvoiceAsync` calls `POST /api/v2/{invoices|sales-receipts}/{id}/approve` before writing anything locally, then reads the doc back for its assigned `document_number`.
- **Factura vs factura simplificada.** An order carrying name **and** address **and** tax id (NIF, a foreign tax id, or a passport number) issues a full `invoice` against an upserted Holded `client` contact. Anything less issues a `sales-receipt` with no contact — and only up to `Store:SimplifiedInvoiceThresholdEur` (default €400). Above it the counterparty details are mandatory and issuance is refused rather than downgraded.
- **Revenue books per item.** Each line carries `items[].account` resolved from its product's `HoldedRevenueAccountNum` against the live chart (Holded's field takes the account *id*, not the number, so the chart is read at issue time). A product with no account number, or a number absent from Holded's chart, refuses issuance by name rather than booking to a catch-all.
- **Deposits post tax-0 to the liability account.** Each deposit-bearing line adds a separate `s_iva_0` line booked to `Store:DepositLiabilityAccountNum` (fianzas). An order with deposits and no configured liability account refuses issuance — a refundable deposit is never booked as income.
- Issuing is **Store-admin only** and never applies to a team order (see Negative Access Rules).
- A Stripe `checkout.session.completed` event with a known `humans_store_order_id` inserts at most one `Payment` per `StripePaymentIntentId` (filtered unique index + service-level dedup check). The inserted row's `Status` is **`Paid`** when `session.payment_status == "paid"` (sync card/wallet) and **`Pending`** otherwise (`"unpaid"` — async mandate captured but not yet cleared, e.g. SEPA).
- **Balance counts `Paid` only.** `BalanceCalculator.Compute` sums payments where `Status == Paid`; `Pending` and `Failed` rows are excluded, so a captured-but-uncleared mandate never makes an order look paid.
- **Async-payment state machine** (`HandleStripeCheckoutWebhookEventAsync`, all idempotent):
  - `checkout.session.async_payment_succeeded` → the matching `Pending` row transitions to `Paid` (`StorePaymentSettled` audit). Re-delivery of an already-`Paid` row is a no-op. Out-of-order delivery (succeeded before `completed`, so no row yet) records a `Paid` payment directly so settled money is never lost.
  - `checkout.session.async_payment_failed` → the matching `Pending` row transitions to `Failed` (`StorePaymentFailed` audit), leaving the order unpaid — never paid-then-reversed. A failure with no matching row is a no-op (no money was ever pending).
  - `checkout.session.expired` → defensively deletes an orphan `Pending` row for the session's PI (`StorePaymentExpired` audit); a `Paid` or `Failed` row is never touched.
- **Reconciliation is the recovery path when the webhook misses a payment** (e.g. `STRIPE_STORE_WEBHOOK_SECRET` unset → webhook 503s). `RecordMissingStripePaymentsAsync` lists Store Checkout Sessions, and records only those that are `payment_status == paid`, resolve to an existing **billable** (non-Team) order via `humans_store_order_id` metadata, and are not already recorded. Amount + PaymentIntent id come **from Stripe** — never fabricated. Idempotent (same PI-id guard as the webhook), so it is safe to re-run. Unmatched sessions and orphan recorded payments are surfaced read-only, never auto-recorded or auto-deleted.
- The treasury sync job (Phase 7, not yet implemented) will match Holded entries to orders **best-effort**; the original `Order.Label` matching key was removed with the Label field (#816), so the eventual matching strategy is TBD.
- Resource-based authorization per design-rules §11: `OrderAuthorizationHandler` + `OrderOperationRequirement` gate Camp Lead writes against the order's parent camp-season. Operations: `View`, `Create`, `AddLine`, `RemoveLine`, `EditCounterparty`, `Pay`, `Delete`, `IssueInvoice`. Mutating ops (`AddLine`, `RemoveLine`, `EditCounterparty`) are gated on `State = Open`; `View` and `Pay` carry no state gate. `Delete` is admin-only (Admin/FinanceAdmin/StoreAdmin on any order; TeamsAdmin on team orders) — camp leads and team coordinators never delete their own orders. A **TeamsAdmin** additionally passes `View` on any order and manages team orders only — `Create` (any department, not just the ones they coordinate: departments buy from the collective too, and this is how that gets tracked) and `AddLine`/`RemoveLine` (Open only); camp orders stay view-only for them, and `EditCounterparty`/`Pay` are never granted on team orders. The **product deadline gate** is also enforced here: when the authorization resource is a `OrderLineContext` (carrying the product's `OrderableUntil`), non-admin line edits (`AddLine` / `RemoveLine`) are denied once today's event-zone date is past the deadline; Store admins are exempt.

## Negative Access Rules

- A Camp Lead **cannot** add or remove lines after the product's `OrderableUntil` has passed (deadline is per-product, enforced by `OrderAuthorizationHandler` at request time; Store admins are exempt).
- A Camp Lead **cannot** edit lines or counterparty on an order in `InvoiceIssued` state.
- A Camp Lead **cannot** view or edit orders for camp-seasons they do not lead (resource-based auth).
- Anyone other than StoreAdmin/FinanceAdmin/Admin **cannot** issue an invoice or run the treasury sync job manually.
- Re-issuing an already-issued order **cannot** succeed — the second call throws and does not contact Holded.
- A camp lead, department coordinator or TeamsAdmin **cannot** issue an invoice — `IssueInvoice` is Store-admin only.
- A team order **cannot** be invoiced, by anyone, including admins.

## Triggers

**Live:**
- Order create, line add/remove, counterparty edit, and Stripe payment record emit audit log entries via `IAuditLogService` (`StoreOrderCreated`, `StoreLineAdded`, `StoreLineRemoved`, `StoreCounterpartyEdited`, `StorePaymentRecorded`). Async-payment transitions emit `StorePaymentSettled` (Pending → Paid), `StorePaymentFailed` (Pending → Failed), and `StorePaymentExpired` (orphan Pending removed on session expiry), all with the `StripeWebhook` job actor.
- Product create, update, and deactivate emit `StoreProductCreated`, `StoreProductUpdated`, `StoreProductDeactivated`. A product update that changes the unit price additionally emits a dedicated, queryable `StoreProductPriceChanged` entry (#816); the order page surfaces these for an order's products since it was created and the catalog edit page shows per-product price history — both through `<vc:audit-log>`, never a Store-side audit read.
- The Stripe webhook controller (`StoreStripeWebhookController`) verifies the request signature via `IStripeService.ParseStoreCheckoutEvent` and dispatches to `Service.HandleStripeCheckoutWebhookEventAsync`, which handles all four `checkout.session.*` events (completed + the async-payment state machine above). Idempotent on `StripePaymentIntentId`.
- `/Store/Admin/Payments/RecordMissing` reconciles Stripe → ledger on demand (admin-triggered), recording missing paid sessions via the same idempotent path and emitting one `StorePaymentsReconciled` summary audit entry (with the human actor) plus the per-payment `StorePaymentRecorded` entries. The webhook is therefore no longer the *sole* writer of Stripe payments — but it remains the only automatic one.

- `IssueInvoiceAsync` (nobodies-collective/Humans#1029) — upserts the Holded `client` contact for an identified counterparty, creates the v2 sales document with per-line revenue accounts, approves it, reads it back, and writes `store_invoices` (both payloads) + the frozen order in one save. Emits `StoreInvoiceIssued` against `StoreInvoice`, cross-referenced to the order.

**Not yet shipped (Phase 5+):**
- Manual payment entry by FinanceAdmin, and the `/Store/Admin/Orders` ledger it would live on. No service member, endpoint, or view exists.
- `StoreTreasurySyncJob` (Hangfire recurring) — would poll `IHoldedClient.ListTreasuryEntriesAsync` from `TreasurySyncState.LastSyncAt`, insert `Payment(Method=BankTransfer)` for unambiguous matches, advance the cursor. No job exists (the original Label matching key was removed in #816). The `store_treasury_sync_state` table and its entity ship, but nothing reads or writes them.

## Cross-Section Dependencies

- **Camps:** `ICampServiceRead` for `CampSeason` lookups (camp name, lead resolution for resource-based auth).
- **Teams:** `ITeamServiceRead` for department lookups (team name, department check via `ParentTeamId is null`, coordinator check via `ManagementRoleHolderUserIds`). Existing methods only — no new surface added to its `[SurfaceBudget(4)]`.
- **Shifts:** `IBurnSettingsService.GetActiveAsync()` for the active event's `Year` and `TimeZoneId` — used to (a) resolve the active catalog year on `/Store` and `/Store/Admin/Catalog`, (b) populate `Year` on new team orders, and (c) compute "today in event time zone" for the `OrderableUntil` deadline gate.
- **Auth/Roles:** `RoleNames.StoreAdmin` (this section), `RoleNames.FinanceAdmin`, `RoleNames.Admin`.
- **Holded connector** (`Humans.Holded`, via `Humans.Holded.Contracts`): `IHoldedClient.UpsertContactAsync`, `CreateSalesDocumentAsync` / `ApproveSalesDocumentAsync` / `GetSalesDocumentAsync` (kind-parameterized over invoice vs sales receipt), and `ListAccountingAccountsAsync` for the account-number → account-id resolution. Never the connector's internals — the vendor stays swappable (`memory/architecture/vendor-connectors-own-sections.md`).
- **Stripe** (`Humans.Stripe`): `IStripeService.CreateCheckoutSessionAsync` for camp-lead payments; `StoreStripeWebhookController` for `checkout.session.completed` ingestion.
- **Audit Log:** `IAuditLogService` for every mutation.

## Stripe Connector

The Store section uses `IStripeService` (`Humans.Stripe.Contracts`; internal impl in `src/Sections/Humans.Stripe/Services/StripeService.cs` — see [Stripe.md](../../Humans.Stripe/Docs/Stripe.md)).

- `STRIPE_STORE_KEY` — `checkout_session:write` (Write ⊇ Read, so it also creates Checkout Sessions **and** lists/reads them for reconciliation via `ListStoreCheckoutSessionsAsync`). Each session is created with `humans_store_order_id` stamped on **both** the session metadata and the PaymentIntent metadata, plus a legible description, so payments are matchable from the dashboard, receipts, and PI search. Refunds, payouts, and chargebacks remain manual via the Stripe dashboard; the bookkeeping side posts as negative `Payment` rows via FinanceAdmin manual entry (Phase 5.3).
- `STRIPE_STORE_WEBHOOK_SECRET` — signing secret for `StoreStripeWebhookController`. Set manually in QA/prod; auto-provisioned at boot in PR-preview envs via `StoreWebhookRegistrationService` (requires `STRIPE_STORE_WEBHOOK_REGISTRAR_KEY`).
- Webhook events subscribed and handled: `checkout.session.completed` (records Paid or Pending by `payment_status`), `checkout.session.async_payment_succeeded` (Pending → Paid), `checkout.session.async_payment_failed` (Pending → Failed), `checkout.session.expired` (orphan-Pending cleanup) — the async-payment state machine (nobodies-collective/Humans#638).
- Boot-time `StripeStartupSmokeService` validates each key with one low-risk read (Checkout.Sessions.list for Store key). Positive-confirmation only — cannot detect over-granted scopes.

## Architecture

**Owning services:** `Service`
**Owned tables:** `store_products`, `store_orders`, `store_order_lines`, `store_payments`, `store_invoices`, `store_treasury_sync_state`
**Status:** (A) Migrated — new section, born §15-compliant (peterdrier/Humans store-foundation, 2026-04-30).
**Project:** `src/Sections/Humans.Store` — the G5 pilot (nobodies-collective/Humans#866). The whole vertical is one assembly: `Domain/ Data/ Services/ Controllers/ Models/ Views/ Authorization/ Docs/ Contracts/` plus `Section.cs`. Everything is `internal` except `Section` and `StoreResource` — including the `Contracts/` folder, which publishes nothing since `IStoreServiceRead` was retired.

- `Service` (`Services/Service.cs`) depends only on Base abstractions — nothing in Store reaches another section's internals.
- `Repository` (`Data/Repository.cs`, §15b Singleton + `IDbContextFactory<StoreDbContext>`) is the only type that touches Store tables. `IStoreRepository` keeps its prefix where the rest of the internals drop theirs, because it derives from `IRepository` and cannot itself be called that (#866 design §6a). Store publishes **no cross-section read contract**: nothing outside the section reads it, so `Service` is `internal` and implements only `IApplicationService`. It had an `IStoreServiceRead` (added for the admin dashboard tile, nobodies-collective/Humans#1264) whose sole caller was ever Store's own `SectionAdminTiles`; that resolves `Service` directly now and the contract was retired, along with the `public` on the DTO graph it returned. Store still has no caching decorator (see below).
- **Decorator decision — no caching decorator.** Store is admin / camp-lead only, low-traffic; same rationale as Budget / Governance.
- **Schema decision — one polymorphic `Order`, not a second table.** Nullable `CampSeasonId` / `TeamId` on the same row was chosen over a separate `store_team_orders` table so team orders reuse the existing catalog, line, authorization and audit machinery instead of duplicating it for the non-billable case. The "exactly one of the two is non-null" invariant is service-enforced, not a DB constraint.
- **Cross-domain navs:** none. `CampSeasonId`, `ProductId`, `AddedByUserId`, `RecordedByUserId`, `IssuedByUserId` are all FK-only with no navigation property. Intra-section back-navs `OrderLine.Order` and `Payment.Order` are aggregate-local and are kept.
- **Cross-section calls** route through `ICampServiceRead` (camp / camp-season lookups), `IBurnSettingsService` (active event year + time-zone), `IAuditLogService`, `IHoldedClient`, `IStripeService`.
- **Architecture test:** none — `StoreArchitectureTests` was deleted at G5. Both its assertions became false or vacuous once the section became one assembly (the assembly now contains `StoreDbContext` by design, and interface-implementation is a tautology). What it encoded is policed by `ApplicationServiceDbContextInjectionAnalyzer` plus the assembly boundary itself. Store's unit tests live in `tests/Humans.Store.Tests`; its controller tests stay in `Humans.Integration.Tests`.

### Configuration

Bound from `Store:*` into `StoreSectionOptions` in `Section.Register`. Both values are
Acountax's call and change without a deploy:

| Key | Default | Meaning |
|---|---|---|
| `Store:DepositLiabilityAccountNum` | unset | Holded chart number of the refundable-deposit (fianzas) liability account. Unset refuses issuance of any order carrying a deposit. |
| `Store:SimplifiedInvoiceThresholdEur` | `400` | Order total at or below which a counterparty-less order may issue as a *factura simplificada*. Spanish law allows €400 generally / €3,000 for retail-type B2C; the conservative figure is the default until Acountax rules. |

Implementation status: catalog CRUD (create, update, deactivate), order create, add/remove line, counterparty edit, Stripe payment recording, and Holded invoice issuance are live. Manual payment entry, treasury sync, and the Orders admin view are unbuilt — no code for them exists. See [`Store-feature.md`](features/Store-feature.md).
