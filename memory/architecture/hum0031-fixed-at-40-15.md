---
name: HUM0031 thresholds are fixed at 40/15 — a backstop, judged by intent elsewhere
description: HUM0031's 40/15 controller-method thresholds are fixed permanently and never ratchet; controller intent is judged by section-doctor, not by moving the numbers.
---

# HUM0031 thresholds are fixed at 40/15

`HUM0031` (`ControllerBusinessLogicAnalyzer`) fires at **> 40 statements** or **cyclomatic
complexity > 15** on a controller method. Those numbers are **fixed permanently** (Peter,
2026-10-21). There is no ratchet: never propose lowering, raising, or "re-evaluating" them.

**Why:** a statement or branch count is a smell, not intent. Ten lines can be domain logic and fifty
can be honest view-model assembly. The numbers only catch the worst offenders; the real question,
"is this controller making a domain decision?", needs judgment, so it lives in section-doctor's
controller-intent check, run on every section regularly, with the litmus *would a Hangfire job doing
the same domain operation need this line?*

**How to apply:**

- Carry 40/15 forward verbatim in any analyzer inventory or doc.
- Found business logic in a controller under the thresholds? It is a finding (move it to the
  section's service), never an argument for tighter numbers.
- A `[Grandfathered("HUM0031", …)]` method is ordinary debt; working it down is fine.
- Intent-shaped analyzer ideas (one narrow pattern each) go to Tier 2 of
  `docs/architecture/roslyn-analysis.md`, not into these thresholds.

Related: [[no-business-logic-in-controllers]] · [[grandfathered-attribute-is-debt]]
