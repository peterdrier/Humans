---
name: Update maintenance log after running any recurring maintenance process
description: After context cleanup, freshness sweep, NuGet check, /simplify pass, etc., update `docs/architecture/maintenance-log.md` with the current date and next-due date.
---

**After running any recurring maintenance process** (context cleanup, feature spec sync, NuGet check, code simplification, etc.), update `docs/architecture/maintenance-log.md` with the current date and next-due date.

**Why:** Without the log, the next session has no way to know what's overdue versus what was just done. The log is what `/maintenance` reads to decide what to prioritize.

**How to apply:**

- After the maintenance task lands, edit `docs/architecture/maintenance-log.md` and bump the row for that task: today's date in "Last Run", today + cadence in "Next Due".
- **Notes stay one line: current state + links.** Never append run narrative, findings, or lessons to a row — that belongs in the run's PR body (or the process's own per-run file). A row that grows past one line is the bug this rule exists to prevent (a 27,000-char cell was the repo's #1 merge-conflict hot-spot, fixed 2026-08-18).
- Commit the log update with the maintenance work itself, not as a separate commit.
- If a maintenance task type isn't yet in the log, add a new row. Completed one-time work never gets a row — PRs record it.
- **Exception: `/section-doctor` runs never touch this log** (nobodies-collective/Humans#1069). Its row is frozen; each run writes its own `docs/health/runs/<date>-<Section>.md` instead — concurrent unattended runs appending to a shared row was a guaranteed merge conflict.
