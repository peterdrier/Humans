# Store — Data Access

## Store

Folder: `src/Sections/Humans.Store/Services/` (namespace
`Humans.Store.Services`). **DbContext:** `StoreDbContext`. Owns
`StoreProducts`, `StoreOrders`, `StoreOrderLines`, `StorePayments`,
`StoreInvoices`, `StoreTreasurySyncStates`.

### StoreService (Scoped)

Repository: `IStoreRepository`.

| Table | R/W |
|-------|-----|
| StoreProducts | R/W |
| StoreOrders | R/W |
| StoreOrderLines | R/W |
| StorePayments | R/W |
| StoreInvoices | R/W |
| StoreTreasurySyncStates | — (table ships; no code reads or writes it) |

Cross-section calls via `IAuditLogService`, `ICampServiceRead`,
`ITeamServiceRead` (team-order counterparty surface),
`IBurnSettingsService`, `IStripeService` (the `Humans.Stripe` connector
section — creates Checkout sessions, lists sessions for reconciliation, handles
webhook events including SEPA async-payment transitions), `IHoldedClient` (the
`Humans.Holded` connector section — contact upsert, sales-document create/approve/read
and the chart-of-accounts read that resolves account numbers to Holded ids), plus
`IClock`. No `IMemoryCache`.

`StoreService` owns the full Stripe **payment flow**: synchronous card/wallet
payments are recorded as `Paid` on `checkout.session.completed`; SEPA/delayed
methods are recorded `Pending` (mandate captured, not yet cleared) and
transitioned to `Paid` / `Failed` via `async_payment_succeeded` /
`async_payment_failed` webhooks; a pending payment blocks a second checkout
to prevent double-charge. `GetStripeReconciliationAsync` pairs live Stripe
Checkout sessions against recorded `StorePayments` and classifies each row
as Recorded / Pending / Unmatched / Missing / Unpaid;
`RecordMissingStripePaymentsAsync` back-fills missing `StorePayments` rows
and writes a `StorePaymentsReconciled` audit entry. **Repricing** — Open
orders reprice live against the single active event-year catalog
(`StoreProducts`), so legacy `Year = 0` orders still reprice correctly.
All surfaces read/write only Store-owned tables through `IStoreRepository`;
reconciliation reads live Stripe sessions via `IStripeService`.

`IssueInvoiceAsync` is the section's only outbound write: it reprices the order's line
snapshots from the live catalog, builds one Holded line per order line (plus a tax-0 line
per deposit), creates **and approves** the document, then writes `StoreInvoices` and the
frozen `StoreOrders` row in a single `SaveIssuedInvoiceAsync` — one `SaveChanges`, because
an order must never be `InvoiceIssued` without its invoice row.

### BalanceCalculator

Stateless calculator — no DI dependencies, no DB access.

---


