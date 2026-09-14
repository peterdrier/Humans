---
name: Section declares its Shell contributions through interfaces
description: Adding a nav link, admin tile, job, policy, health check, endpoint, or chrome/dashboard component for a section → prefer implementing the ISectionContribution seam on Section itself; a separate internal Section<Seam> class is the fallback; never a by-name edit to a Shell composition class.
---

Prefer `Section.cs` implementing `ISection` plus the applicable `ISectionContribution` interfaces (`ISectionNav`, `ISectionAdminNav`, `ISectionAdminTiles`, `ISectionChrome`, `ISectionMemberDashboard`, `ISectionThingsToDo`, `ISectionJobs`, `ISectionHealthChecks`, `ISectionEndpoints`, `ISectionPolicies`), so the section's composition capabilities are declared in one place. `Register` remains DI registration; contribution methods supply descriptors, and business logic stays in services. Consolidation of the existing separate classes is nobodies-collective/Humans#1088.

**Why:** `SectionDiscoveryExtensions.RegisterContributions` finds every `ISectionContribution` implementation by the same dependency-graph walk `ISection` uses (`DiscoverImplementations<T>()`, which walks `GetTypes()` rather than `GetExportedTypes()`) and registers it as a singleton against every seam interface it implements. That walk already picks up seams implemented on `Section`, and the public-surface analyzer already allows the `ISection` entry point, so the consolidated shape costs no Shell or analyzer change. A new seam interface deriving from the marker costs Shell no edit; a by-name edit to a Shell composition class isn't discovered by anything and reopens the compile-time coupling nobodies-collective/Humans#1073 removed. Where a separate class is used, the `Section` prefix isn't decoration: a bare `Jobs` class collides with the section's own `Jobs` namespace (`class Jobs` in `Humans.Consent` is CS0101 against `Humans.Consent.Jobs`), and because discovery is reflection-only over `GetTypes()` the class stays `internal` without tripping HUM0034.

**How to apply:**

- Implement existing contribution interfaces on `Section`. Use explicit interface implementations when identical signatures need different results.
- Keep the entry point parameterless and contributions stateless. Discovery creates instances separately from the instance used for `Register`; do not rely on registration-time instance state.
- Split larger implementations into partial files or separate `internal sealed Section<Seam>` contribution classes when that improves readability. Separate contributions must also be parameterless and stateless; contribute each item from exactly one shape.
- Never edit Shell's composition classes (`AdminNavComposition`, `SectionNavViewComponent`, `ChromeSlotViewComponent`, `RecurringJobExtensions`, `AuthorizationPolicyExtensions`, `Program.cs`) to name a section directly. Implementing an existing seam changes only the contributing section; a new seam may need generic consumer support.

See [design-rules.md §8b](../../docs/architecture/design-rules.md#8b-cross-section-fanout--contributor-pattern) for the seam catalog and ordering rules.
