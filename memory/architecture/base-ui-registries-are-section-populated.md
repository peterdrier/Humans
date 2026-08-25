---
name: A Base UI registry holding section vocabulary is section-populated, never section-referencing
description: A shared `Humans.UI` lookup table that would otherwise need to name a section's enum instead gets its rows pushed in from that section's `Section.Register` — Base never gains a reference to a section's contracts leaf just to keep naming the type. (Peter, 2026-08-09.)
---

`Humans.UI` is Base. It holds cross-cutting presentation machinery — the table widget, the badge
map, the status-badge extensions — and several of those are **registries keyed by section enums**.
Since a section's enums live inside its own project, Base cannot name them directly.

**The fix is to invert the direction: the section pushes its rows into the registry from
`ISection.Register`.** The registry exposes a `Register(...)` method and a section adds one call
to its `Section.Register` to populate its own rows. Nothing in Base names a section type. A row
that a registry cannot avoid holding literally — because the value is genuinely Base's own
vocabulary, not a section's — stays a literal entry (`EnumBadgeMap`'s one remaining literal row is
`EmailOutboxStatus`).

**Why not the cheap answer.** Promoting the enum into `<Section>.Contracts` and having `Humans.UI`
reference that leaf is smaller in the moment and correct for exactly one section. Applied ten times
it ends with Base holding a project reference to every section's vocabulary, which is the coupling
the split exists to remove — locally cheapest, globally worst. Same verdict for
`Humans.Base`: it is the right home for an enum **two sections genuinely branch on**, and the
wrong home for one whose only cross-boundary use is a colour lookup. The test is whether the
*type* is shared or the *concern* is; the mechanism belongs where the shared concern is.

**How to apply:**

- Base registry needs to name a section's type ⇒ add a `Register(...)` to the registry and have
  the section push its own rows from `Section.Register`; leave other sections' literals alone.
- **Make `Register` idempotent per row.** A process composes the service collection more than once
  — `WebApplicationFactory` builds a host per integration-test class, section architecture tests
  call `Register` against a throwaway `ServiceCollection` — while a static registry outlives all of
  them. Re-registering the same key/value is normal and must be a no-op; throw only when the same
  key arrives with a *different* value, which is two owners disagreeing.
- A static registry is acceptable **only** where the read path genuinely has no DI to inject
  through — `TableColumn<TRow>.Badge` renders from a static method. Prefer an injected singleton
  anywhere DI is reachable.
- **Pin the rows with a test in the section**, asserting the resolved value per enum member.
  `EnumBadgeMap.For` falls back to `bg-secondary` for an unregistered value, so a forgotten
  registration renders a grey badge that no build, suite or rendered-HTML diff will flag.
- If the extension method or helper has **no callers outside the section**, it is not a registry
  problem at all — move it into the section and delete it from Base.
  `StatusBadgeExtensions.GetBadgeClass(ExpenseReportStatus)` had exactly two call sites, both in
  Expenses' own views.

`Humans.Shifts`'s `ShiftPeriod`/`SignupStatus` rows are registered from `Humans.Shifts/Section.cs`
and pinned by `ShiftsArchitectureTests.SectionRegistersABadgeClassForEveryShiftPeriodAndSignupStatus`.
Base's `StatusBadgeExtensions` was retired outright: its only two call sites were Users' views, so
it now lives in `Humans.Users/Extensions/` as an `internal` class rather than a registry. Budget's
`BudgetYearStatus` overload is likewise a section-local extension rather than an
`EnumBadgeMap.Register` row, because its three admin views call `GetBadgeClass()` directly and the
map only serves `CellFormat.EnumBadge` table columns.

Related: [`section-project-cycle-fix`](section-project-cycle-fix.md),
[`sections-are-logical-units`](sections-are-logical-units.md),
[`view-components-vs-partials`](../code/view-components-vs-partials.md).
