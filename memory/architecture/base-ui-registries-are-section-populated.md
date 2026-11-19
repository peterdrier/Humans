---
name: base-ui-registries-are-section-populated
description: When a Base `Humans.UI` registry (EnumBadgeMap etc.) needs a section's enum, have the section push rows via `Section.Register` — Base never references the section's contracts.
---

Base holds presentation registries keyed by section enums (the badge map, status-badge extensions) but
cannot name a section's types. **Invert the direction: the registry exposes `Register(...)` and each
section pushes its own rows from `Section.Register`.** Only a value that is Base's own vocabulary stays
a literal row.

**Why not promote the enum** into the section's Contracts (or Base) and reference it: correct for one
section, and ten sections later Base references every section's vocabulary, the coupling the split
exists to remove. Base is the home for an enum two sections genuinely branch on, not for one whose only
cross-boundary use is a colour lookup.

**How to apply:**
- **`Register` is idempotent per row.** The service collection is composed many times per process
  (a host per integration-test class, architecture tests) while a static registry outlives them all.
  The same key and value again is a no-op; the same key with a different value throws.
- A static registry is acceptable only where the read path has no DI (`TableColumn<TRow>.Badge` renders
  from a static method). Prefer an injected singleton wherever DI is reachable.
- **Pin the rows with a test in the section** (`<Section>BadgeRowsTests`), asserting the value per enum
  member: an unregistered value falls back to a grey badge that nothing else flags.
- Views render enum statuses with `<status-badge value="@x.Status" />`. A helper keyed on non-enum input
  (a percentage, a string) stays where it is.
- A helper with no callers outside one section is not a registry problem: move it into the section.

**Related:** [`section-project-cycle-fix`](section-project-cycle-fix.md) ·
[`sections-are-logical-units`](sections-are-logical-units.md)
