---
name: Nightly debt runs stop at time or quota boundaries
description: "Nightly debt work selects the latest origin/main runner per run, uses Sol with optional Luna helpers, requires discovery before batching simple corrections and reasons for splits, checks cumulative weekly quota between committed rounds, and reports cleanup/repair/subagent spend."
---

Work in validated, committed rounds for at most 12 hours, finishing the active
batch afterward. The wrapper checks live weekly quota before the first round
and after each completed round. Stop new work if remaining quota falls below
`100 - 12 * elapsed_days_since_weekly_reset`, or quota is unavailable. This
leaves 16% at week's end; all account usage counts. Manual runs may explicitly
set `IGNORE_QUOTA_CUTOFF=1`; it bypasses quota checks, never the 12-hour cap or
validation. Never consume reset credits automatically.

Use one native goal session across rounds. Complete the goal after each clean,
committed batch and return a cumulative report. The wrapper reactivates that
same goal only while time and quota allow another round. No fix-count target.
One PR contains all independently validated fixes. Work existing ledger rows
first ([[debt-ledger-must-shrink]]). Ledger-only or test-only work cannot qualify
a run for publication. Final build/test and publication may extend past the
work deadline. Report real fixes, validation, elapsed time, and stop reason.

**Production work first:** Standalone coverage, controller-policy pins, test
scaffolding, and test cleanup are never sweep objectives. Add or update focused
tests when a production-code fix warrants them, as part of that fix; prefer
existing coverage when sufficient. Never weaken checks. Batch simple, verified
instances of the same correction into one coherent commit; validate all affected sections and applicable rung
checks once per completed batch. Keep complex or approval-dependent changes
separate. Before editing, discover related instances across the owning section
and shared consumers, and state the candidates and shared validation plan.
Include verified compatible siblings; justify splitting them by semantics, risk,
review scope, or time remaining. For a singleton, state the searched scope.
Carry deferred candidates forward and record split reasons in the report.
Freeze membership before editing and stop adding candidates at the
deadline. Run the full non-integration suite once in the final wrapper gate,
not repeatedly per commit. Test-only work cannot qualify a run for publication.

**Model routing:** Require Codex CLI 0.159.2 or newer. Default to
`gpt-6.1-sol` / medium for the coordinator.
Optional `gpt-6-luna` / medium helpers get narrow, fresh-context briefs for
bounded discovery, localization, or decided mechanical edits. The coordinator
verifies findings, reviews edits, and owns builds/tests, Git, and the goal.
Keep helper edits disjoint and finish them before validation; use direct work
when delegation adds overhead or is unavailable.

**Spend visibility:** After creating the PR, post the Codex spend skill report
using recorded, explicit cleanup and gate-repair session IDs. Include linked
subagents; never infer the run from the reporting chat's current session. Keep
repair breakdowns separate. Label costs as Standard API equivalents, not actual
subscription charges. Reporting failure must not block fix publication.

**Scheduled runner version:** The installed launcher fetches the clone's origin
main before each start and extracts matching cron tools and prompts into a
versioned release outside the disposable clone. Keep that release for the run;
fetch failures stop startup without stale fallback. Merged worker changes take
effect next run; launcher/service changes require reinstallation.
