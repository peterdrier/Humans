---
name: The section list comes from DI — don't hand-maintain another one
description: Before hand-writing a list of section names — in code, in a doc, or in a skill's ledger — inject `ISectionCatalog` or glob `src/Sections/Humans.*`. A deliberate subset publishes `ISectionAnnotations` so drift shows.
---

**Don't hand-maintain a list of section names.** `ISectionCatalog` is a singleton in `Humans.Base.Interfaces`, built by `SectionCatalogBuilder` from the same dependency-graph walk that registers the sections, so it cannot drift from what the app runs. Inject it. It carries, per section, everything derived from the assembly: `IsActive`, `DependsOn`, `Seams`, `DbContexts`, `ServiceInterfaces`, `Repositories`, `HasContracts`, `HasResources`. `TryResolve` canonicalizes casing, which is what you want before building a path, a cache key or a stored column value.

**Why:** separate copies of the section list existed before nobodies-collective/Humans#1509 — `IssueSectionRouting.AllKnownSections`, `AgentSectionKeys.Canonical`, `GuideFiles.Sections` — and some had already fallen behind renames without anything noticing: `Profiles` merged into Users at #866, `Legal` became Consent at the 2026-08-03 freeze. Nothing fails when a list goes stale — a routing table keyed by string keeps routing under the old name, and an agent key that names no section just dead-ends into the community FAQ — so drift stays invisible for years.

**How to apply:**

- New code that needs to know what sections exist → inject `ISectionCatalog`. Never a new const list.
- A list that is a **deliberate subset** — the agent's user-facing keys, the sections that have an issue queue — stays owned by its section. The catalog does not replace it and must not be used to derive it: `IsKnown` answers "is this a section", not "may this section be named here".
- Any such subset publishes an `ISectionAnnotations` contribution (`internal sealed class SectionAnnotations` at the section root, one `SectionAnnotation` per entry). An entry naming no discovered section lands in `ISectionCatalog.UnmatchedAnnotations`, is logged at startup and is shown at `/Debug/Sections`.
- Drift is a **warning, never a startup failure**. Nothing fails at runtime on a stale entry, and throwing would make a section rename un-shippable.
- Validating a stored section string → validate against the list that owns the *behaviour* (`IssueSectionRouting` for an issue's queue), not against the catalog. The set of sections and the set of queues are not the same set.

## Docs and skill ledgers: same rule, no catalog

Sections are independent, so there is no app-wide roster of them in prose either. Do not add a
section row to `docs/architecture/freshness-catalog.yml`, `docs/architecture/dependency-graph.md`,
or any other repo-wide listing just because a section exists and is missing from it — a missing row
is not a defect, and filling one in is not maintenance. Peter, 2026-09-16: "sections are
independent, we shouldn't have app wide listings of them. the skills need to be able to discover
them dynamically. `/sections/Humans.*/foo` definitely do not add."

**How to apply:**
- A skill or script that needs every section → glob `src/Sections/Humans.*` (and the per-section
  path under it, e.g. `src/Sections/Humans.*/Docs/<Section>.md`). Never a checked-in name list.
- Noticed a section absent from a repo-wide ledger → that is the ledger's design, not a finding.
  Don't add the row, don't raise it ([`no-tests-for-absences`](no-tests-for-absences.md) covers
  why absence is not a question).
- A ledger row that *carries state* the glob cannot derive — a last-swept date, an opt-out — is a
  deliberate subset and keeps its row; it is the bare "this section exists" roster that is banned.

**Related:** [`design-rules.md` §8b](../../docs/architecture/design-rules.md) (the seam table), [`base-ui-registries-are-section-populated`](base-ui-registries-are-section-populated.md), [`no-tests-for-absences`](no-tests-for-absences.md).
