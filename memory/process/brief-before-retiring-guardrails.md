---
name: brief-before-retiring-guardrails
description: Never retire an analyzer, architecture test, ratchet, or guardrail without Peter's explicit approval. Ask, name the guardrail, wait for his go.
---

Never retire an analyzer, architecture test, ratchet, or other guardrail without Peter's explicit approval. Name the guardrail, say why you think it should go, and wait for his go.

**Why:** deleting enforcement is one-way and easy to get wrong from inside a refactor — the premise that looks dead often isn't. In a 2026-08-15 analyzer sweep, one of three analyzers flagged for deletion turned out to be the only compile-time enforcement of a hard rule and was correctly kept.

**How to apply:** approval is the gate, not a written form. One line per guardrail, batched in one message. Peter 2026-09-14, striking the old four-line-brief requirement: "no brief… you just need my approval." Do not dispatch the retirement itself until he answers — but non-retirement work in the same area (adding loud-failure assertions, measuring coverage) can proceed meanwhile. Don't report a finding as "filed" if it only went into a gitignored notes file Peter can't see — say where it actually went.

Related: [[retirement-first-for-subsumed-guardrails]] says *prefer* retirement when structure subsumes a guardrail — this rule governs *how* that gets confirmed; the two aren't in conflict.
