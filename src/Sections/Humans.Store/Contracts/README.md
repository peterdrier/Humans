# Contracts

Everything in this folder is `public`; outside it only two types are — `Section` and
`StoreResource`, the latter because the boot localization diagnostic discovers resource
markers via `GetExportedTypes()`. Everything else is `internal`. This folder holds a
section's cross-section surface only: `I<Section>ServiceRead`, canonical read DTOs, and
domain events.

`IStoreServiceRead` exposes `GetStoreSummaryAsync` for the admin dashboard tile
(nobodies-collective/Humans#1264 tile wave), plus the DTO graph it returns
(`SummaryDto`, `OrderSummaryDto`, `ProductAggregateDto`, `CrossTabDto`/`CrossTabColumn`/
`CrossTabRow`) and the two enums that graph exposes (`OrderCounterpartyType`,
`OrderState`) — HUM0034 requires any type reachable from a public member to live here too.
