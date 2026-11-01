<!-- freshness:triggers
  src/Sections/Humans.Store/**
-->

# Store — Data Access

## Store

Folder: `src/Sections/Humans.Store/Services/` (namespace
`Humans.Store.Services`). **DbContext:** `StoreDbContext`. Owns
`store_products`, `store_orders`, `store_order_lines`, `store_payments`,
`store_invoices`.

### Service (Scoped — `IApplicationService`)

The section's internal Store facade (`Services/Service.cs`). Repository: `IStoreRepository`.
Its admin summary delegates to `StoreOrderReader`; accounting consumers resolve the reader directly.

| Table | R/W |
|-------|-----|
| store_products | R/W |
| store_orders | R/W |
| store_order_lines | R/W |
| store_payments | R/W |
| store_invoices | R/W |

Cross-section calls via `IAuditLogService`, `ICampService`,
`ITeamService` (team-order counterparty surface),
`ISettingsService`, `IUserService` (current-user resolution in the controllers), `IStripeService` (the `Humans.Stripe` connector
section — creates Checkout sessions, lists sessions for reconciliation, handles
webhook events including SEPA async-payment transitions), `IHoldedClient` (the
`Humans.Holded` connector section — contact upsert, sales-document create/approve/read
and the chart-of-accounts read that resolves account numbers to Holded ids), plus
`IClock`. No `IMemoryCache`.

The service owns the full Stripe **payment flow**: synchronous card/wallet
payments are recorded as `Paid` on `checkout.session.completed`; SEPA/delayed
methods are recorded `Pending` (mandate captured, not yet cleared) and
transitioned to `Paid` / `Failed` via `async_payment_succeeded` /
`async_payment_failed` webhooks; a pending payment blocks a second checkout
to prevent double-charge. `GetStripeReconciliationAsync` pairs live Stripe
Checkout sessions against recorded `store_payments` and classifies each row
as Recorded / RecordedPending / RecordedFailed / Missing / Unmatched / Unpaid;
`RecordMissingStripePaymentsAsync` back-fills missing `store_payments` rows
and writes a `StorePaymentsReconciled` audit entry. **Repricing** — Open
orders reprice live against the single active event-year catalog
(`store_products`), so legacy `Year = 0` orders still reprice correctly.
All surfaces read/write only Store-owned tables through `IStoreRepository`;
reconciliation reads live Stripe sessions via `IStripeService`.

Legacy `store_orders.Year = 0` rows are reviewed and repaired through
`/Store/Admin/OrderYears`. `IStoreRepository.GetOrdersWithMissingYearAsync` reads the
Store-owned candidates; the service resolves each cross-section season through
`ICampService`, updates only resolvable rows, and audits every confirmed repair.

`IssueInvoiceAsync` delegates to `StoreInvoiceIssuer`; live pricing and counterparty
names come from `StoreOrderReader`. The service constructs both helpers with its
`IStoreRepository`; accounting reads also resolve `StoreOrderReader` through `IStoreAccountingRead`.

### StoreInvoiceIssuer (internal — `IApplicationService`, constructed per issuance by `Service`)

Repository: `IStoreRepository`.

| Table | R/W |
|-------|-----|
| store_products | R (`GetProductsByIdsAsync` — includes inactive products; each line's Holded revenue account) |
| store_invoices | W (`SaveIssuedInvoiceAsync`) |
| store_orders | W (`SaveIssuedInvoiceAsync` — frozen `InvoiceIssued` row) |
| store_order_lines | W (repriced line snapshots, same save) |

Cross-section calls via `IHoldedClient` (the section's only outbound Holded write) and
`IAuditLogService`, plus `IClock`, `IOptions<StoreSectionOptions>` and the shared
`StoreOrderReader`. Issuance reprices the order's line snapshots from the live catalog,
builds one Holded line per order line (plus a tax-0 line per deposit), creates **and
approves** the document, then writes `store_invoices` and the frozen `store_orders` row in
a single `SaveIssuedInvoiceAsync` — one `SaveChanges`, because an order must never be
`InvoiceIssued` without its invoice row. No `IMemoryCache`.

### StoreOrderReader (internal — `IStoreAccountingRead`, Scoped)

Repository: `IStoreRepository`.

| Table | R/W |
|-------|-----|
| store_products | R (catalog and year-wide pricing) |
| store_orders | R (index, summary and accounting export) |
| store_order_lines | R (effective prices and quantities) |
| store_payments | R (settled accounting payments and balances) |
| store_invoices | R (accounting invoice numbers) |

The year summary and accounting exports share one year-loading and pricing step.
Cross-section calls via `ISettingsService` (current event year), `ICampService` /
`ITeamService` (counterparty display names), plus `IClock`. No writes, no `IMemoryCache`.

### StoreStripePayments (internal — `IApplicationService`, constructed once by `Service`)

Repository: `IStoreRepository`.

| Table | R/W |
|-------|-----|
| store_payments | R/W (PaymentIntent dedupe, add, status update, delete) |
| store_orders | R (`GetOrderByIdAsync` — refuses team-owned orders) |

Records Stripe payments from checkout webhooks under a process-wide `TrackedLock`.
Cross-section calls via `IAuditLogService`, plus `IClock`. No `IMemoryCache`.

### BalanceCalculator

Stateless calculator — no DI dependencies, no DB access.

---

