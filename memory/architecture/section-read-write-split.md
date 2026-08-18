---
name: section-read-write-split
description: Sections consumed by other sections expose a *Read interface containing only DTO/Info-returning methods used cross-section; writes, cache hooks, and section-internal reads stay on the full interface
metadata:
  type: project
---

# Section read/write interface split

When a section's service is consumed by code in other sections, that service exposes two interfaces:

- `I<Section>ServiceRead` — only the methods external sections actually call, returning section-owned projections (DTOs / `*Info` types), never EF entities.
- `I<Section>Service : I<Section>ServiceRead` — the full surface: writes, cache-invalidation hooks, entity-returning reads, and reads that only the owning section's own code uses.

Same implementation class implements both. DI registers both interfaces pointing at the same singleton/scope.

**Why:** Outside sections shouldn't depend on EF entities of another section (couples them to that section's storage shape, defeats nav-strip work) and shouldn't see write methods that aren't theirs to call. Enforced by analyzer HUM0032.

**How to apply:**
- Trigger is **cross-section consumption**, not the presence of a caching decorator.
- The read interface is the *minimum* surface external sections call. Section-internal reads that happen to be consumed only by the section's own controllers/services stay on the full interface.
- Methods returning EF entities (`Team`, `TeamMember`, etc.) never go on `*Read`. If an external caller needs entity-shaped data, that's a signal the section's projection (`TeamInfo`, etc.) is missing a field — fix the projection, don't expose the entity.
- Cache-invalidation hooks (e.g. `InvalidateActiveTeamsCache`) stay on the full interface — they're writes against cache state. Eventually these become event-driven and go away.
- **The read interface is the default; a cross-section write is the flagged exception.** HUM0032 (`Internal/Rules/CrossSectionReadRule.cs`) fails any class injecting another section's write-capable `I*Service` that has an `I*ServiceRead` base — it reads the declaration, not the usage, so "I only call reads today" is not a defence. Either demote the parameter, or mark the class `[CrossSectionWrite("what it writes and why")]`. Prefer neither: when another section needs a write, pull in the owning section's view component and let it write its own data. Read-interface *content* hygiene (DTOs only) is [[service-read-no-ef]], now structural rather than analyzer-enforced on every `.Contracts` leaf except `Humans.Users.Contracts`, which deliberately declares the EF `User` entity and is unguarded for this property. Each new read split arms HUM0032 for its section automatically.

**Reference implementation:** Teams (`ITeamServiceRead` / `ITeamService`).

See also: `docs/architecture/design-rules.md` §11 (authorization), `docs/sections/SECTION-TEMPLATE.md` (cross-section read interface section).
