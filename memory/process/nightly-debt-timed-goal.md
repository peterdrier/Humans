---
name: Nightly debt runs use the whole work window
description: Nightly debt work prioritizes production-code fixes, uses tests only to support fixes, uses the full timed goal, and publishes one PR.
---

Work actively for the full configured window, then finish the current task
before completing the goal. One PR contains multiple independently validated
fixes. Time is the only target: never give this worker a fix-count quota.

**Why:** The first trial stopped after 89 seconds and only deleted a stale
ledger row. Successful pipeline plumbing was mistaken for useful debt work.

**How to apply:** Use one native Codex goal session across turn boundaries.
Preserve its objective, deadline, and dangerous permissions if the harness
must continue it. An early completion must reactivate the same goal after the
current turn finishes, then continue with the actual clock and original deadline;
it must not fail the run or publish early. Do not kill the task
at the work deadline or reserve early wind-down time. Ledger cleanup and
documentation do not count as substantive fixes; a ledger-only run cannot
publish a debt PR. Report real fixes, validation, elapsed time, and skipped
candidates. Counts describe the result; they never determine when to stop.

**Production work first:** Standalone coverage, controller-policy pins, test
scaffolding, and test cleanup are never sweep objectives. Add or update focused
tests when a production-code fix warrants them, as part of that fix; prefer
existing coverage when sufficient. Never weaken checks. Run scoped validation
per change and the full non-integration suite once in the final wrapper gate,
not repeatedly per commit. Test-only work cannot qualify a run for publication.
