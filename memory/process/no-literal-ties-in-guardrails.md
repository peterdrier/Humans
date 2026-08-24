---
name: no-literal-ties-in-guardrails
description: Never endorse a test/guardrail that pins a path, URL, or assembly list as a literal — derive it from the registry (the app is composed by reflection) or don't write it.
---

A guardrail that names its subject as a **literal** — a file path, a hand-maintained URL list, an assembly-name string, a namespace string — is fragile by construction and creates work forever. It doesn't prevent the move it names; it just fails when someone makes the move, and the only fix is editing the literal. Peter: "literal ties like that are so fragile, and only serve to make more work for us in the long run."

**Why:** Humans is composed **dynamically by reflection** in a growing number of places — `ISection`, `[assembly: Section]`, `SectionControllerFeatureProvider`, MVC application parts. A test that restates that composition as a list can't detect a composition bug, and rots on every section added. The repo has already lost coverage this way four separate times via namespace / `typeof(X).Assembly` anchors that went silently dead when code moved, with a green build each time.

**How to apply:**
- Derive the set from the registry — enumerate `ISection` / `[assembly: Section]` / application parts — never restate it by hand. If a test enumerates sections, pages, or assemblies by hand, that's the defect.
- Prefer an **analyzer** for call-site rules (Peter's stated preference; tests aren't acceptable for rules that fit the analyzer pattern). Where Roslyn can't reach (`.cshtml`), derive the set by scanning, not by listing.
- Anchor on a **type**, not a string, and assert the anchor's assembly identity so a move fails loudly — `typeof(Section).Assembly` is immune by construction.
- Don't praise a guardrail merely for avoiding the known-bad shape (e.g. a vacuous `NotContain(...)`). Judge it by: what does this do when the thing it names legitimately moves?

Related: [[our-rules-are-movable]] · [[retirement-first-for-subsumed-guardrails]] · [[brief-before-retiring-guardrails]].
