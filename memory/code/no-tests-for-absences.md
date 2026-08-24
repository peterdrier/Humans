---
name: no-tests-for-absences
description: never write a test asserting a section does NOT have something (no DbContext, no repository, no localizer, no resource set, exports only N types); an absence has no behaviour to regress, and the list of things a section doesn't have is unbounded
---

Never write a test whose assertion is that a section *lacks* something.

No `SectionTakesNoDbContextOrRepository`, no `NoTypeInTheSection_TakesADbContextOrStore`,
no `ServiceTakesNoRepository`, no "binds no `IStringLocalizer<T>`", no "exports only these
four types". If the section doesn't have the thing, it doesn't have it. That's the whole
story.

**Why:** The list of things a section does not contain is unbounded — a section could
plausibly have 50 different kinds of thing, so a small section that owns three files could
accumulate 45 "no xyz" tests, each one green because nobody wrote code. That is not
coverage; it's a second copy of the file listing, maintained by hand.

An absence also has no failure mode to catch. Regression tests exist because behaviour can
drift silently. Nothing drifts here: if someone adds a `DbContext` to a section that had
none, they did it deliberately and in the same commit they update the test. The test never
warns anyone — it just makes the deliberate change cost one more edit.

**The trap that generates these:** a section doc claims an architecture test enforces
something, and the test doesn't exist. The doc is wrong. Delete the false claim — do not
write the test to make the sentence true. A doc sentence is not a specification.

**How to apply:**
- About to assert `Should().BeEmpty()` / `BeNull()` / `NotContain()` over a section's *types,
  constructors, dependencies or exports*? Stop — that's this rule.
- Found a doc claiming a pinning test that isn't there? Fix the doc, not the test file.
- Genuinely need the constraint enforced? It's a universal analyzer, not a per-section test
  — see [`universal-enforcement-over-per-section`](../architecture/universal-enforcement-over-per-section.md)
  and Peter's hard rule that analyzers beat tests for call-site rules.

**Not this rule** — these assert real behaviour and stay:
- A query returning no rows for an input that should match nothing.
- A localizer *binding* check (every call site's key exists in the set that site binds) —
  a misbind renders the raw key with a green build and no log line. Real defect, real test.
- An authorization negative: this role gets `404`/`AccessDenied` on this route. That's a
  behaviour the code actively produces.

The distinction: does the code *do* something you're checking, or are you checking that
code was never written?

**Related:** [`universal-enforcement-over-per-section`](../architecture/universal-enforcement-over-per-section.md)
