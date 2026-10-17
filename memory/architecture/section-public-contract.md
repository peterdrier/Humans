---
name: section-public-contract
description: A section's Contracts are its whole public face — `I<Section>Service` (DTO-only reads), `I<Section>Write` only where a cross-section write is unavoidable, everything else internal `I<Section>Management`.
metadata:
  type: project
---

# A section's Contracts are its whole public face

Anything public in a section lives in `Contracts/` (or the `.Contracts` leaf); everything else is `internal`. Three interface roles:

- `I<Section>Service` — the public read contract. DTO-only: no EF entities, no `IQueryable` ([[service-read-no-ef]]). Other sections inject this.
- `I<Section>Write` — public, and only where another section genuinely must write. The exception, not the default.
- `I<Section>Management` — internal. Everything else: the section's own writes, cache hooks, entity-returning reads. It may inherit the public faces; one implementation class, DI registers each face to the same instance.

**Why:** One boundary beats two. The old `I<Section>ServiceRead` / `I<Section>Service` split policed "which interface may you inject" with a naming-keyed analyzer and an attribute escape hatch; both were deleted and nothing replaces them. Location now carries the rule: HUM0034 (public iff Contracts) and HUM0037 (no EF in Contracts) enforce it, plus the entity-return ratchet in `Humans.Web.Tests`.

**How to apply:**
- Need another section's data → its `I<Section>Service`. Missing a field → fix the DTO, never expose the entity.
- Need to write into another section → prefer the owning section's own view component or GUI control, so the write stays internal ([[view-component-slots]]). Add `I<Section>Write` only when that cannot work; say why in the PR, and keep it to the members callers use.
- New public members are debt ([[interface-method-additions-are-debt]]); Contracts is the promise, so keep it narrow and documented.
- Exception: AuditLog is a write-by-default crosscut and keeps its own shape.

**Related:** [[section-identity-is-the-assembly]], [[section-project-cycle-fix]], `docs/sections/SECTION-TEMPLATE.md` (Public contract).
