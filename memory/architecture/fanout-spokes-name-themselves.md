---
name: A fanout hub never enumerates its spokes
description: When adding/changing a fanout interface (contributor pattern, hub iterates IEnumerable<IContract>), never add a central constant, enum, or type list naming which sections implement it — spokes declare their own facts, tests derive the roster by reflection.
---

A **fanout** is one interface in a `.Contracts` leaf that many sections implement and one
orchestrator/hub aggregates via `IEnumerable<IContract>` (see [`orchestrator-marker`](orchestrator-marker.md)
and [[crosscut-purity]]'s "fan-out contributions" mechanism). The hub must never hold — in Base,
the `.Contracts` leaf, the Shell, or an architecture test — a central constant table, enum, or
hard-coded `Type[]` naming which sections participate. That list is itself a hub-names-spokes
coupling: every new spoke requires editing code it doesn't own, and a spoke that moves or renames
silently drops out unless someone remembers to update the list.

**Why:** `Humans.Gdpr.Contracts.GdprExportSections` held ~50 `public const string` fields, one per
section's export key, so `Humans.Gdpr` — a hub with 20+ spokes — named every spoke's vocabulary
centrally. Renaming or adding a section's export key meant editing the hub's file. Worse,
`GdprExportDependencyInjectionTests.ExpectedContributorTypes` hard-coded the full contributor
`Type[]`, so the "which sections implement this" question was answered by a list a human had to
remember to touch, not by discovery. Both were removed (nobodies-collective/Humans#1116): each
contributor now declares its own section-name constants beside the class that uses them, an
`ErasesLast` default-interface-member replaced a hub-side `.ContainsKey("Account")` check, and both
tests derive their expected roster by reflecting over `SectionDiscoveryExtensions.SectionAssemblies()`.

**How to apply:**

- Adding a new fanout, or a new spoke to an existing one: the spoke declares its own keys/constants
  on its own contributor class (or a small holder beside it), never on a type the hub owns.
- If the hub needs to treat one spoke specially (e.g. "run this one last"), add a member to the
  interface with a safe default (`bool Flag => false;`) that the special spoke overrides — never a
  hub-side check naming that spoke's constant or type. Default interface members are used this way
  elsewhere (`ISection.IsActive => true;`).
- Architecture/coverage tests over a fanout derive their "expected roster" by reflecting over the
  same section assemblies the runtime composes from (`SectionDiscoveryExtensions.SectionAssemblies()`),
  never a pinned list. A test that hard-codes `ExpectedXTypes` is the same anti-pattern as the
  register it's meant to guard.
- A completeness check that needs "every declared X has a matching Y" (e.g. every exported section
  has an erasure declaration) should be enforced **per spoke, at runtime, by the hub itself**
  wherever possible (the hub already sees both sides for the spoke it's calling), so the
  architecture test is only left with cross-spoke checks (duplicates, at-least-one-of, formatting).

Related: [[crosscut-purity]], [[orchestrator-marker]], [`base-ui-registries-are-section-populated`](base-ui-registries-are-section-populated.md) (the same inversion for a Base registry instead of a hub).
