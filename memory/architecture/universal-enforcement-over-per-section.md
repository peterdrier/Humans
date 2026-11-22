---
name: universal-enforcement-over-per-section
description: When adding/reviewing an architecture analyzer or arch test, it must be universal (derived from a hard rule), never per-section — a per-section enforcer or workaround is itself a smell.
---

Architecture enforcement is **universal**: derived from a hard rule and applied to every section the
same way. A per-section analyzer or per-class test is a smell that resolves to one of:

- **Redundant** — duplicates a universal enforcer → delete it.
- **A gap** — a real hard rule with no universal enforcer → build that, then delete the instances.
- **A genuine one-off** domain invariant → keep, only after proving it cannot be stated generally.

**Why:** per-section enforcers multiply without bound and drift into different shapes of the same rule.
The corollary: a workaround mechanism is itself the warning. A `SaveChangesInterceptor`, a dual-writer
allowlist, a per-class "is-not-present" tombstone each exist because a boundary wasn't closed; closed
properly, the workaround has nothing to do. The rule is "none should exist", not "this one stays gone".

**How to apply:**
- About to write an analyzer or arch test scoped to one section, table, column or class (the name is the
  trigger)? Find the universal rule it instances and enforce that once, keyed off convention
  (namespace, `[Section]`, the ownership map in the analyzers' `Internal/Sections.cs` +
  `Internal/SectionDbContexts.cs`), never a hardcoded name list.
- **Don't invent the universal rule to justify a consolidation.** If the hard rules, `design-rules.md`,
  an atom, an existing `Rules/*.cs` or a `HUM####` analyzer doesn't state it, it is a proposal that
  needs a decision before any enforcer or deletion.
- Severity: ship `Error` plus `[Grandfathered("HUM####", …)]` on the finite existing violators. No
  baselines, blanket downgrades, `Info` or `NoWarn` for live architecture debt —
  [`no-analyzer-suppressions`](../process/no-analyzer-suppressions.md).
- "This symbol must exist" wiring checks and DI-singleton checks can't be analyzers; consolidate them
  into one universal reflection test, not a copy per section.

**Related:** [`no-tests-for-absences`](no-tests-for-absences.md) ·
[`crosscut-purity`](crosscut-purity.md) · [`section-public-contract`](section-public-contract.md) ·
[`repository-required-for-db-access`](repository-required-for-db-access.md)
