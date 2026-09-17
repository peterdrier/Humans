# Contracts

**Store publishes one cross-section surface:** `IStoreAccountingRead` (peterdrier/Humans#1719),
the year-filtered order-line and payment export Backdoor serves at `/api/backdoor/store/*`, with
its two DTOs (`AccountingOrderLineDto`, `AccountingPaymentDto`) and the `OrderCounterpartyType`
/ `OrderState` enums they carry. Everything else in this folder is `internal`, like the rest of
the section; outside it only two more types are `public` — `Section` and `StoreResource`, the
latter because the boot localization diagnostic discovers resource markers via
`GetExportedTypes()`.

It held `IStoreServiceRead` and the DTO graph that interface returned, added for the admin
dashboard tile (nobodies-collective/Humans#1264 tile wave). No other section ever called it —
the one consumer was Store's own `SectionAdminTiles`, which resolves `Service` directly now, so
the contract was retired rather than maintained as a promise nobody had asked for.

The summary DTOs (`SummaryDto`, `OrderSummaryDto`, `ProductAggregateDto`,
`CrossTabDto`/`CrossTabColumn`/`CrossTabRow`) stay here, `internal`, rather than moving to
`Services/Dtos`, so the day another section needs them the graph is already assembled in the
folder that would publish it. Making them `public` is a one-word change per type, plus the
interface.
