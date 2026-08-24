---
name: prefer-base-class-for-cross-cutting
description: When adding a cross-cutting concern (diagnostics, telemetry, lifecycle hooks) to ≥2 already-similar classes, propose a shared base class first — fall back to a new interface only if base-class extraction genuinely can't work.
---

When adding cross-cutting concerns (diagnostics, telemetry, common lifecycle behavior) across multiple classes that already share a shape, default to a shared base class — not a new interface.

**Why:** Peter overruled an interface-based proposal for cache-decorator diagnostics with "generic base class is better, especially since you love to write new crap all the time and need to be controlled." Several caching decorators already shared a shape (same field pattern, same dictionary/scope-factory approach); the "shapes diverge too much, use an interface" argument was the wrong call. Inheritance forces consistency where consistency is wanted; a new interface just adds surface without enforcing shape.

**How to apply:**
- When ≥2 sealed classes follow the same pattern and a new cross-cutting concern arrives, propose a base class first, and only fall back to an interface if base-class extraction genuinely can't work (incompatible shapes, not just "different members exist on top").
- Don't argue against a base class just because the classes have section-specific helpers on top — those stay on the derived class.
- General rule this serves: prefer fewer abstractions. A new interface is a new abstraction; a base class is a refactor of existing ones.

Related: [[prefer-setup-work-for-trivial-usage]] — same family, applied to setup-cost vs call-site-cost tradeoffs.
