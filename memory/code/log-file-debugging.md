---
name: Debug runtime errors via the log file first — don't speculate
description: When debugging runtime errors, grep the log file first — don't speculate. Write diagnostic logs with entity IDs and actual values, not just "operation failed".
---

When debugging runtime errors, **always check the log file first** before speculating about causes. Serilog console sink is always enabled via `WriteTo.Console()`.

Use `Grep` on log output filtering by entity ID, error keywords, or timestamp. Write diagnostic log messages (`_logger.LogWarning`/`LogError`) that include entity IDs, actual values, and expected values — not just "operation failed". When something goes wrong, the log should tell you *why*.

**Related:** [`always-log-problems`](always-log-problems.md).
