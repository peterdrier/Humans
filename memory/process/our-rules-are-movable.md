---
name: our-rules-are-movable
description: Self-authored analyzers/conventions/architecture-test predicates are options, not walls — never argue "X forbids it"; give the reason behind the rule and let Peter weigh moving it.
---

Never present a project-authored rule as an external constraint. Analyzers (HUM00xx), conventions in design docs, section-gate predicates, and architecture-test baselines were all written by this project and can be changed, relaxed, or deleted. Saying "the keystone analyzer forbids public entity types" is a non-argument. Peter: "we can move types if need be, there's no forbids, we own this whole project."

**Why:** dressing up a self-imposed convention as an external wall hides the actual trade-off and rules out the cheap option before he's seen it. Moving a type, relaxing a predicate, or dropping an analyzer rule is always on the menu.

**How to apply:** give the *reason behind* the rule, not the rule. "HUM0029 forbids it" → "this is a mutable EF-tracked entity, so passing it across a boundary carries tracking semantics into another section — that's what HUM0029 was written for." Then let him weigh it. When the rule genuinely is load-bearing, the reason will carry the argument on its own; when it isn't, he gets the cheap path.

**The one exception:** [`peters-hard-rules.md`](../../docs/architecture/peters-hard-rules.md). Those are hand-maintained by Peter, never edited by an LLM, and win every conflict. Everything else — including anything an agent or a design doc introduced — is negotiable and should be surfaced as such.

Related: [[no-literal-ties-in-guardrails]], [[retirement-first-for-subsumed-guardrails]].
