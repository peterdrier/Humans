---
name: section-list-from-di
description: "Before hand-writing a list of section names in code, a doc or a skill ledger: inject `ISectionCatalog` or glob `src/Sections/Humans.*` instead."
---

**Never hand-maintain a list of section names.**

**In code:** inject `ISectionCatalog` (`Humans.Base.Interfaces`). It is built from the same
dependency-graph walk that registers the sections, so it cannot drift, and carries what is derived from
each assembly (dependencies, seams, DbContexts, service interfaces, repositories). `TryResolve`
canonicalizes casing; use it before building a path, cache key or stored value.

- A **deliberate subset** (the agent's user-facing keys, the sections with an issue queue) stays owned
  by its section and is not derived from the catalog. It publishes an `ISectionAnnotations`
  contribution; an entry naming no discovered section lands in `UnmatchedAnnotations`, is logged at
  startup and shows on `/Debug/Sections`.
- Drift is a **warning, never a startup failure**: throwing would make a section rename un-shippable.
- Validate a stored section string against the list that owns the *behaviour* (e.g.
  `IssueSectionRouting` for an issue queue), not against the catalog.

**In docs, skills and scripts:** glob `src/Sections/Humans.*`. There is no app-wide roster in prose
either. Peter: *"sections are independent, we shouldn't have app wide listings of them. the skills need
to be able to discover them dynamically."*

- A section missing from a repo-wide ledger is that ledger's design, not a finding. Don't add the row,
  don't raise it ([`no-tests-for-absences`](no-tests-for-absences.md)).
- A ledger row carrying state the glob cannot derive (a last-swept date, an opt-out) is a deliberate
  subset and keeps its row.

**Why:** hand-kept copies fell behind section renames and nothing failed: a routing table kept routing
under the old name for years.

**Related:** [`base-ui-registries-are-section-populated`](base-ui-registries-are-section-populated.md)
