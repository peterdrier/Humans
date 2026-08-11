---
name: A Base UI registry holding section vocabulary becomes section-populated, never section-referencing
description: When a shared `Humans.UI` lookup table names a section's enum and that section moves out at G5, the section registers its rows from `Section.Register` — Base must not gain a reference to the section's contracts leaf to keep naming the type. (Peter, 2026-08-09.)
---

`Humans.UI` is Base. It holds cross-cutting presentation machinery — the table widget, the badge
map, the status-badge extensions — and several of those are **registries keyed by section enums**:
literal tables naming `CampaignStatus`, `ExpenseReportStatus`, `TicketPaymentStatus`, `VoteChoice`
and six more. That compiles today only because every one of those enums still sits in
`Humans.Domain.Enums`. Each G5 move (nobodies-collective/Humans#866) takes one out, and the
registry stops compiling.

**The fix is to invert the direction: the section pushes its rows into the registry from
`ISection.Register`.** The registry keeps a literal table for the sections that have not moved and
gains a `Register(...)` method; the moving section deletes its rows from Base and adds one call to
its `Section.Register`. Nothing in Base names a section type, and the endgame — all ~35 moved — is
an empty literal.

**Why not the cheap answer.** Promoting the enum into `<Section>.Contracts` and having `Humans.UI`
reference that leaf is smaller in the moment and correct for exactly one section. Applied ten times
it ends with Base holding a project reference to every section's vocabulary, which is the coupling
the split exists to remove — locally cheapest, globally worst. Same verdict for
`Humans.Interfaces`: it is the right home for an enum **two sections genuinely branch on**, and the
wrong home for one whose only cross-boundary use is a colour lookup. The test is whether the
*type* is shared or the *concern* is; the mechanism belongs where the shared concern is.

**How to apply:**

- Base registry names a moving section's type ⇒ add a `Register(...)` to the registry, move the
  rows into `Section.Register`, leave the other sections' literals alone.
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

First applied 2026-08-09 by Expenses' move (peterdrier/Humans#1240), for `EnumBadgeMap`.
`StatusBadgeExtensions` still carries `ApplicationStatus`; that moves when Governance does.
Budget took its `BudgetYearStatus` overload with it at its G5 move — an extension in the
section rather than an `EnumBadgeMap.Register` row, because its three admin views call
`GetBadgeClass()` directly and the map serves `CellFormat.EnumBadge` table columns.

Related: [`section-project-cycle-fix`](section-project-cycle-fix.md),
[`sections-are-logical-units`](sections-are-logical-units.md),
[`view-components-vs-partials`](../code/view-components-vs-partials.md).
