# Contracts — deliberately empty

Everything in this folder is `public`; everything outside it (except `Section`) is
`internal`. It holds a section's cross-section surface only: `I<Section>ServiceRead`,
canonical read DTOs, and domain events.

Store has none. Nothing in the codebase consumes Store — its fan-in is Shell alone — so
there is no read surface to expose, and an empty folder is the honest end state for a leaf
section rather than an omission (design §9 B4, §15.5b). `Contracts/` is earned, not
mandatory.
