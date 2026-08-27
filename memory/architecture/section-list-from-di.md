---
name: The section list comes from DI — don't hand-maintain another one
description: Before writing a list of section names as consts, an array or a HashSet, inject `ISectionCatalog` (`Humans.Base.Interfaces`). Shell publishes the real section set at startup from the dependency-graph walk. A list that is a deliberate subset (agent doc keys, issue queues) stays owned by its section, but publishes an `ISectionAnnotations` contribution so drift shows up on `/Debug/Sections` instead of degrading in silence.
---

**Don't hand-maintain a list of section names.** `ISectionCatalog` is a singleton in `Humans.Base.Interfaces`, built by `SectionCatalogBuilder` from the same dependency-graph walk that registers the sections, so it cannot drift from what the app runs. Inject it. It carries, per section, everything derived from the assembly: `IsActive`, `DependsOn`, `Seams`, `DbContexts`, `ServiceInterfaces`, `Repositories`, `HasContracts`, `HasResources`. `TryResolve` canonicalizes casing, which is what you want before building a path, a cache key or a stored column value.

**Why:** separate copies of the section list existed before nobodies-collective/Humans#1509 — `IssueSectionRouting.AllKnownSections`, `AgentSectionKeys.Canonical`, `GuideFiles.Sections` — and some had already fallen behind renames without anything noticing: `Profiles` merged into Users at #866, `Legal` became Consent at the 2026-08-03 freeze. Nothing fails when a list goes stale — a routing table keyed by string keeps routing under the old name, and an agent key that names no section just dead-ends into the community FAQ — so drift stays invisible for years.

**How to apply:**

- New code that needs to know what sections exist → inject `ISectionCatalog`. Never a new const list.
- A list that is a **deliberate subset** — the agent's user-facing keys, the sections that have an issue queue — stays owned by its section. The catalog does not replace it and must not be used to derive it: `IsKnown` answers "is this a section", not "may this section be named here".
- Any such subset publishes an `ISectionAnnotations` contribution (`internal sealed class SectionAnnotations` at the section root, one `SectionAnnotation` per entry). An entry naming no discovered section lands in `ISectionCatalog.UnmatchedAnnotations`, is logged at startup and is shown at `/Debug/Sections`.
- Drift is a **warning, never a startup failure**. Nothing fails at runtime on a stale entry, and throwing would make a section rename un-shippable.
- Validating a stored section string → validate against the list that owns the *behaviour* (`IssueSectionRouting` for an issue's queue), not against the catalog. The set of sections and the set of queues are not the same set.

**Related:** [`design-rules.md` §8b](../../docs/architecture/design-rules.md) (the seam table), [`base-ui-registries-are-section-populated`](base-ui-registries-are-section-populated.md).
