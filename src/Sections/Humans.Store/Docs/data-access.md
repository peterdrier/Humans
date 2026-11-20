<!-- freshness:triggers
  src/Sections/Humans.Store/**
-->

# Store — Data Access

## Store

Folder: `src/Sections/Humans.Store/Services/` (namespace
`Humans.Store.Services`). **DbContext:** `StoreDbContext`. Owns
`store_products`, `store_orders`, `store_order_lines`, `store_payments`,
`store_invoices`, `store_settings`.

### Service (Scoped — `IApplicationService`)

The section's internal Store facade (`Services/Service.cs`). Repository: `IStoreRepository`.
Implements `IStoreAccountingRead`; accounting consumers resolve the same instance.

| Table | R/W |
|-------|-----|
| store_products | R/W |
| store_orders | R/W |
| store_order_lines | R/W |
| store_payments | R/W |
| store_invoices | R/W |
| store_settings | R/W (`GetSettingsAsync` / `SaveSettingsAsync`, the `/Settings#store` tab) |

Cross-section calls via `IAuditLogService`, `ICampService`,
`ITeamService` (team-order counterparty surface),
`ISettingsService`, `IUserService` (current-user resolution in controllers and order authorization), `IStripeService` (the `Humans.Stripe` connector
section — creates Checkout sessions, lists sessions for reconciliation, handles
webhook events including SEPA async-payment transitions), `IHoldedService` / `IHoldedWrite`
(the `Humans.Holded` section — contact create, sales-document issue/approve/tag search, and the
chart sync that confirms each account number exists), plus
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

`IssueInvoiceAsync` reprices the order's line snapshots from the live catalog, builds one Holded
line per order line (plus one 21% line per product's kept deposit), creates **and approves** the
document, then writes `store_invoices` and the frozen `store_orders` row in a single
`SaveIssuedInvoiceAsync` (one `SaveChanges`: an order is never `InvoiceIssued` without its invoice
row). Stripe payments from checkout webhooks and missing-payment recovery go through one idempotent
recording path under a process-wide `TrackedLock`.

### StoreTransferBooking (internal — `IApplicationService`, Scoped)

Owns no table access of its own: reads team-order lines through `IStoreAccountingRead`. Cross-section calls via
`IBudgetService` (active year's department categories), `IFinanceService` (category → Holded account map),
`IHoldedService` (transfer-account ledger mirror and its re-pull), `IHoldedWrite.PostJournalEntryAsync`, and
`IAuditLogService`, plus `IClock`. No `IMemoryCache`.

### BalanceCalculator

Stateless calculator — no DI dependencies, no DB access.

---

