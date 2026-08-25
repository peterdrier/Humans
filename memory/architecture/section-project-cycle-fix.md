---
name: A section-project cycle is fixed by splitting contracts downward, never by promoting the shared type up
description: When MSBuild refuses a section project reference as circular, carve `<Section>.Contracts` out of the UPSTREAM section. Never move the shared type into a Base project to make the error go away — that is how Base silently becomes a global blob.
---

The tier model is `Shell → Section → Base`: Shell owns composition and nav, a section owns its
vertical, Base holds what they share. Nothing in Base references a section; no section references
Shell. Cross-section dependencies are ordinary project references, so a cycle is an MSBuild error
(`MSB4006` / "circular dependency") rather than an analyzer finding — the compiler is the boundary.

When you hit one, there is exactly one correct fix and one tempting wrong one.

**Correct — split the upstream section's contracts downward.** Section A needs a type from section
B, and B already references A. Carve `Humans.B.Contracts`: a leaf project holding only B's
`IBServiceRead`, its canonical read DTOs and its domain events, referencing Base and nothing else.
`Humans.B` references `Humans.B.Contracts` and implements it; `Humans.A` references
`Humans.B.Contracts` only. The cycle is gone and B's implementation is still invisible to A.

**Wrong — promote the shared type into Base.** It makes the error go away in one commit and it is
irreversible in practice: every promoted type is one more thing that "everyone can see", and within
a handful of sections Base is the monolith with extra steps. `Humans.Base` is primitives and
marker interfaces only — **no DTOs, ever**, and it is human-gatekept for exactly this reason. A
cycle is not a reason to put a DTO there.

Do not pre-split. A `.Contracts` project is a signal that a section sits in a dependency knot, so
it should only appear where the build actually demanded it. `Users` and `Teams` are the predicted
knots; nothing else is split in advance.

**How to apply:**

- Cycle build error ⇒ ask which of the two sections is upstream (the one more things read from),
  and split *that* one's contracts. Do not reverse the dependency to dodge the split.
- The `.Contracts` project references Base only. If it needs another section, you have found a
  second cycle, not an exception.
- Moving a type into `Humans.Base` (or the deleted `Humans.Domain` / `Humans.UI` projects) to resolve a cycle needs
  Peter's per-instance approval. "The build was red" is not the justification.
- The standing shared-contract exceptions are `User`/`UserInfo`, Auth and Audit.
  Those are the whole list; a cycle does not add to it.
- Full mechanics and the pilot plan: `docs/superpowers/specs/2026-08-07-g5-section-project-split-design.md`.

Related: [[sections-are-logical-units]], [[section-read-write-split]],
[[universal-enforcement-over-per-section]], [[interface-method-additions-are-debt]].
