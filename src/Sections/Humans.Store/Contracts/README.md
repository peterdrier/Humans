# Contracts — deliberately empty

Everything in this folder is `public`; outside it only two types are — `Section` and
`StoreResource`, the latter because the boot localization diagnostic discovers resource
markers via `GetExportedTypes()`. Everything else is `internal`. This folder holds a
section's cross-section surface only: `I<Section>ServiceRead`, canonical read DTOs, and
domain events.

Store has none. Nothing in the codebase consumes Store — its fan-in is Shell alone — so
there is no read surface to expose, and an empty folder is the honest end state for a leaf
section rather than an omission (design §9 B4, §15.5b). `Contracts/` is earned, not
mandatory.
