---
name: fanout-spokes-name-themselves
description: "Adding or changing a fanout interface (hub iterates `IEnumerable<IContract>`): no central constant, enum or type list of participating sections; spokes declare themselves."
---

A **fanout** is one contract interface that many sections implement and one hub aggregates through
`IEnumerable<IContract>`. Nothing the hub owns (its contracts, Base, the Shell, an architecture test)
may hold a central constant table, enum or `Type[]` naming which sections participate.

**Why:** that list is hub-names-spokes coupling. Every new spoke edits code it doesn't own, and a spoke
that moves or renames silently drops out. The GDPR export hub once held a constant per section's export
key and a test pinned the contributor type list.

**How to apply:**
- A spoke declares its own keys and constants beside its contributor class.
- The hub needs to treat one spoke specially ("run this one last")? Add an interface member with a safe
  default (`bool ErasesLast => false;`) that the spoke overrides. Never a hub-side check on that spoke's
  name or type.
- Tests over a fanout derive the expected roster by reflecting over
  `SectionDiscoveryExtensions.SectionAssemblies()`, never a pinned list.
- "Every declared X has a matching Y" is checked per spoke, at runtime, by the hub, which sees both
  sides. Architecture tests keep only cross-spoke checks (duplicates, at-least-one, formatting).

**Related:** [`orchestrator-marker`](orchestrator-marker.md) · [`crosscut-purity`](crosscut-purity.md) ·
[`base-ui-registries-are-section-populated`](base-ui-registries-are-section-populated.md) (the same
inversion for a Base registry)
