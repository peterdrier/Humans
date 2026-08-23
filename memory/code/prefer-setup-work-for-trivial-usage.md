---
name: prefer-setup-work-for-trivial-usage
description: When choosing between a heavier-setup abstraction with trivial call sites and a lighter-setup one with verbose call sites, prefer the heavier setup — call sites multiply, the abstraction is written once.
---

When designing an API/abstraction with N call sites, prefer the option that makes the call sites trivial — even if the setup (definition, registration, supporting types) is more work.

**Why:** Peter: "we always prefer more setup work and trivial one liners at usage. That's much better for tech debt." Call sites multiply — more today, more tomorrow — while the abstraction is written once. Verbose call sites accrue ongoing cost: every reader parses them, every refactor touches them, every new addition mimics them. Heavier setup is a one-time cost.

**How to apply:**
- A tag helper beats a partial view — more C# scaffolding, one-line usage at the call site.
- A custom analyzer/source-generator beats hand-written boilerplate at every call site.
- A small base class with shared infra beats duplicating the same lines at each subtype.
- A typed tag-helper attribute beats untyped string parameters even if the helper code grows.
- When presenting options that trade setup-cost against call-site-cost, frame the recommendation around call-site cost, and don't apologize for setup work.

Related: [[prefer-base-class-for-cross-cutting]] — same principle, applied to inheritance vs. interface choice.
