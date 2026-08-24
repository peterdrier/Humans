---
name: retirement-first-for-subsumed-guardrails
description: When a structural migration subsumes a guardrail (an analyzer/arch-test covering what a new structure now enforces by construction), prefer retiring it over generalizing it to the new shape.
---

During a structural migration (e.g. the per-section project split), the instinct is often to widen analyzers and architecture tests to also cover the new shape. That's frequently wrong: most such violations become obsolete once assembly boundaries, per-section DbContexts, and internal-by-default accessibility make the violation impossible or a compile error — the right outcome is deletion, not widening.

**Why:** a guardrail exists to enforce what the structure can't. When the structure starts enforcing it (project refs, accessibility, separate contexts), keeping or widening the guardrail is maintenance debt with zero enforcement value.

**How to apply:** in any audit/fix pass over guardrails during a boundary migration, evaluate "does the new structure subsume this?" BEFORE "how do I make this cover the new layout?" Retirement is a first-class outcome, often the preferred one. Ask which way when unclear — don't preservation-bias.

Related: [[brief-before-retiring-guardrails]] governs how a retirement gets confirmed once decided; [[no-new-ci-checks]].
