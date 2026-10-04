---
name: Nightly debt runs use the whole work window
description: Nightly debt work uses Sol with optional Luna helpers, batches simple corrections, prioritizes production fixes, and uses the full timed goal.
---

Work actively for the full configured window, then finish the current task
before completing the goal. One PR contains multiple independently validated
fixes. Time is the only stopping rule: never give this worker a fix-count quota.
The target is the ledger, which must end smaller ([[debt-ledger-must-shrink]]).

**Why:** The first trial stopped after 89 seconds and only deleted a stale
ledger row. Successful pipeline plumbing was mistaken for useful debt work.

**How to apply:** Use one native Codex goal session across turn boundaries.
Preserve its objective, deadline, and dangerous permissions if the harness
must continue it. An early completion must reactivate the same goal after the
current turn finishes, then continue with the actual clock and original deadline;
it must not fail the run or publish early. Do not kill the task
at the work deadline or reserve early wind-down time. Stale-row deletion and
documentation do not count as substantive fixes (fixing a row's code does); a ledger-only run cannot
publish a debt PR. Report real fixes, validation, elapsed time, and skipped
candidates. Counts describe the result; they never determine when to stop.

**Production work first:** Standalone coverage, controller-policy pins, test
scaffolding, and test cleanup are never sweep objectives. Add or update focused
tests when a production-code fix warrants them, as part of that fix; prefer
existing coverage when sufficient. Never weaken checks. Batch simple, verified
instances of the same correction into one coherent commit; validate all affected sections and applicable rung
checks once per completed batch. Keep complex or approval-dependent changes
separate. Freeze membership before editing and stop adding candidates at the
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
