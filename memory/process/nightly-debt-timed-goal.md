---
name: Nightly debt runs use the whole work window
description: When changing or running the nightly debt worker, require a timed native goal, multiple substantive fixes, and one PR per run.
---

Work actively for the full configured window, then finish the current task
before completing the goal. One PR contains multiple independently validated
fixes. Time is the only target: never give this worker a fix-count quota.

**Why:** The first trial stopped after 89 seconds and only deleted a stale
ledger row. Successful pipeline plumbing was mistaken for useful debt work.

**How to apply:** Use one native Codex goal session across turn boundaries.
Preserve its objective, deadline, and dangerous permissions if the harness
must continue it. Do not kill the task
at the work deadline or reserve early wind-down time. Ledger cleanup and
documentation do not count as substantive fixes; a ledger-only run cannot
publish a debt PR. Report real fixes, validation, elapsed time, and skipped
candidates. Counts describe the result; they never determine when to stop.
