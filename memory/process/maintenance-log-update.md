---
name: Update maintenance log after running any recurring maintenance process
description: After context cleanup, freshness sweep, NuGet check, /simplify pass, etc., update `docs/architecture/maintenance-log.md` with the current date and next-due date.
---

**After running any recurring maintenance process** (context cleanup, feature spec sync, NuGet check, code simplification, etc.), update `docs/architecture/maintenance-log.md` with the current date and next-due date.

**Why:** Without the log, the next session has no way to know what's overdue versus what was just done. The log is what `/maintenance` reads to decide what to prioritize.

**How to apply:**

- After the maintenance task lands, edit `docs/architecture/maintenance-log.md` and bump the row for that task: today's date in "Last Run", today + cadence in "Next Due".
- Commit the log update with the maintenance work itself, not as a separate commit.
- If a maintenance task type isn't yet in the log, add a new row.
