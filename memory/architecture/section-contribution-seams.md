---
name: Section declares its Shell contributions through interfaces
description: When adding a nav link, admin tile, job, or health check, implement it as an `ISectionContribution` seam on `Section` — never a by-name edit to a Shell composition class.
---

`Section.cs` implements `ISection` plus every `ISectionContribution` interface the section needs (`ISectionNav`, `ISectionAdminNav`, `ISectionAdminTiles`, `ISectionChrome`, `ISectionMemberDashboard`, `ISectionThingsToDo`, `ISectionJobs`, `ISectionHealthChecks`, `ISectionEndpoints`, `ISectionPolicies`, `ISectionHelp`, `ISectionDataMigrations`, `ISectionRoles`, `IUserPart`, …), each as an explicit interface implementation, so the section's composition capabilities are declared in one place. `Register` remains DI registration; contribution members supply descriptors, and business logic stays in services. Sibling `Section<Seam>.cs` classes were folded in (nobodies-collective/Humans#1088); do not reintroduce them.

**Why:** `SectionDiscoveryExtensions.RegisterContributions` finds every `ISectionContribution` implementation by the same dependency-graph walk `ISection` uses (`DiscoverImplementations<T>()`, which walks `GetTypes()` rather than `GetExportedTypes()`) and registers it as a singleton against every seam interface it implements. That walk already picks up seams implemented on `Section`, and the public-surface analyzer already allows the `ISection` entry point, so the consolidated shape costs no Shell or analyzer change. A new seam interface deriving from the marker costs Shell no edit; a by-name edit to a Shell composition class isn't discovered by anything and reopens the compile-time coupling nobodies-collective/Humans#1073 removed.

**How to apply:**

- Implement existing contribution interfaces on `Section` as explicit interface implementations: they stay off the public surface, and identical signatures (`ISectionChrome.Components()` / `ISectionMemberDashboard.Components()`) coexist.
- Keep the entry point parameterless and contributions stateless. Discovery creates instances separately from the instance used for `Register`; do not rely on registration-time instance state.
- File-local helpers a seam needs (a `PillCounts` static class, a response record) stay in `Section.cs` beside the class. Do not add sibling `Section<Seam>.cs` classes.
- Never edit Shell's composition classes (`AdminNavComposition`, `SectionNavViewComponent`, `ChromeSlotViewComponent`, `RecurringJobExtensions`, `AuthorizationPolicyExtensions`, `Program.cs`) to name a section directly. Implementing an existing seam changes only the contributing section; a new seam may need generic consumer support.

See [design-rules.md §8b](../../docs/architecture/design-rules.md#8b-cross-section-fanout--contributor-pattern) for the seam catalog and ordering rules.
