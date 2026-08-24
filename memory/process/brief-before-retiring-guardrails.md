---
name: brief-before-retiring-guardrails
description: Before retiring any analyzer, architecture test, ratchet, or other guardrail, give Peter a short brief (title / what it did / purpose / why it's no longer valid) and wait for his go.
---

Never retire an analyzer, architecture test, ratchet, or other guardrail without first giving Peter a short brief per item: title, what it did, what its purpose was, and why it is no longer valid. Then wait for his go.

**Why:** deleting enforcement is one-way and easy to get wrong from inside a refactor — the premise that looks dead often isn't. In a 2026-08-15 analyzer sweep, one of three analyzers flagged for deletion turned out to be the only compile-time enforcement of a hard rule and was correctly kept. Peter: "I want a short brief on anything before it's retired… just to be double sure."

**How to apply:** four lines per guardrail, not a paragraph. Batch them in one message. Do not dispatch the retirement itself until he answers — but non-retirement work in the same area (adding loud-failure assertions, measuring coverage) can proceed meanwhile. Don't report a finding as "filed" if it only went into a gitignored notes file Peter can't see — say where it actually went.

Related: [[retirement-first-for-subsumed-guardrails]] says *prefer* retirement when structure subsumes a guardrail — this rule governs *how* that gets confirmed; the two aren't in conflict.
