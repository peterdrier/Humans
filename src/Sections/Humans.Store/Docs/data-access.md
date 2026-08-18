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
| StoreTreasurySyncStates | R/W |

Cross-section calls via `IAuditLogService`, `ICampServiceRead`,
`ITeamServiceRead` (team-order counterparty surface),
`IShiftManagementService`, `IStripeService` (the `Humans.Stripe` connector
section — creates Checkout sessions, lists sessions for reconciliation, handles
webhook events including SEPA async-payment transitions), plus `IClock`.
No `IMemoryCache`.

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

### BalanceCalculator

Stateless calculator — no DI dependencies, no DB access.

---


