# Contracts

**Store publishes no cross-section surface today.** Everything in this folder is `internal`,
like the rest of the section; outside it only two types are `public` — `Section` and
`StoreResource`, the latter because the boot localization diagnostic discovers resource markers
via `GetExportedTypes()`.

It held `IStoreServiceRead` and the DTO graph that interface returned, added for the admin
dashboard tile (nobodies-collective/Humans#1264 tile wave). No other section ever called it —
the one consumer was Store's own `SectionAdminTiles`, which resolves `Service` directly now, so
the contract was retired rather than maintained as a promise nobody had asked for.

The summary DTOs (`SummaryDto`, `OrderSummaryDto`, `ProductAggregateDto`,
`CrossTabDto`/`CrossTabColumn`/`CrossTabRow`) and the two enums (`OrderCounterpartyType`,
`OrderState`) stay here rather than moving to `Services/Dtos` and `Domain`, so the day another
section does need a read contract, its DTO graph is already assembled in the folder that would
publish it. Making them `public` again is then a one-word change per type, plus the interface.
