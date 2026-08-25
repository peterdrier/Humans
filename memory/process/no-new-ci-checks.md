---
name: Never propose new CI checks
description: HARD RULE. Do not propose, suggest, or add CI checks, build gates, or workflow steps. Builds already take far too long. Report the finding and stop — Peter decides if anything gets automated.
metadata:
  type: feedback
---

**Never propose adding a CI check, build gate, analyzer-in-CI step, or workflow job.** Not as a
recommendation, not as a "worth considering", not as a line in a sweep report or maintenance log,
not as a follow-up item.

Report what you found and stop there.

**Why:** the builds already take far too long, and every added gate makes that worse for every PR
forever. A recurring annoyance is not automatically worth a permanent build-time tax. Peter decides
what gets automated; he does not need it re-pitched.

There is also usually a second reason the pitch is wrong: the thing being "caught" is often
**transient churn from an in-flight migration**, not a permanent condition. The dead
`freshness:triggers` globs that three consecutive freshness sweeps proposed CI for were a direct
product of the section-project-split moves relocating files — once the moves finished, the churn
stopped on its own. Automating a permanent check against a temporary condition is exactly
backwards.

**How to apply:**

- Found something recurring and mechanical? Fix this instance, note it plainly, move on.
- Writing a sweep/audit report? State the finding. Do **not** append "this wants a CI check" or
  any equivalent.
- Before framing anything as "should be automated", ask whether the underlying churn is a migration
  in progress. If it is, the answer is to finish the migration, not to gate on the symptom.

Related: [[hum0031-frozen-until-2027]] — same shape of mistake, re-proposing something Peter has
already settled.
