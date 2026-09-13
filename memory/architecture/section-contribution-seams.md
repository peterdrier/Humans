---
name: Section declares its Shell contributions through interfaces
description: Adding section navigation, tiles, jobs, policies, health checks, endpoints, or chrome/dashboard components → prefer implementing the contribution interfaces on Section; keep Shell composition generic.
---

Prefer `Section.cs` implementing `ISection` plus the applicable `ISectionContribution` interfaces, such as `ISectionNav` and `ISectionJobs`. This keeps the section's composition capabilities together. `Register` remains DI registration; contribution methods supply descriptors, and business logic stays in services.

**Why:** Discovery finds every concrete implementation and registers it against each contribution interface it implements. It already supports multiple interfaces on `Section`; separate classes are optional. The public `ISection` entry point is already allowed by the public-surface analyzer.

**How to apply:**

- Implement existing contribution interfaces on `Section`. Use explicit interface implementations when identical signatures need different results.
- Keep the entry point parameterless and contributions stateless. Discovery creates instances separately from the instance used for `Register`; do not rely on registration-time instance state.
- Split larger implementations into partial files or separate `internal sealed Section<Seam>` contribution classes when that improves readability. Separate contributions must also be parameterless and stateless; register each contribution only once.
- Never edit Shell composition to name a section directly. Implementing an existing seam should require changes only in the contributing section; a new seam may require generic consumer support.

See [design-rules.md §8b](../../docs/architecture/design-rules.md#8b-cross-section-fanout--contributor-pattern) for the seam catalog and ordering rules.
