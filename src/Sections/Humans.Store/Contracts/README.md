# Contracts

**Store publishes one cross-section surface:** `IStoreAccountingRead` (peterdrier/Humans#1719),
the year-filtered order-line and payment export Backdoor serves at `/api/backdoor/store/*`, with
its two DTOs (`AccountingOrderLineDto`, `AccountingPaymentDto`) and the `OrderCounterpartyType`
/ `OrderState` enums they carry. Everything else in this folder is `internal`, like the rest of
the section; outside it only two more types are `public` — `Section` and `StoreResource`, the
latter because the boot localization diagnostic discovers resource markers via
`GetExportedTypes()`.

The summary DTOs (`SummaryDto`, `OrderSummaryDto`, `ProductAggregateDto`,
`CrossTabDto`/`CrossTabColumn`/`CrossTabRow`) stay here, `internal`, rather than moving to
`Services/Dtos`, so the day another section needs them the graph is already assembled in the
folder that would publish it. Making them `public` is a one-word change per type, plus the
interface. Store's own `SectionAdminTiles` resolves `Service` directly for its admin dashboard
tile.
