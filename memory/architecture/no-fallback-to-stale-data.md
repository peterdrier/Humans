---
name: No fallback to stale data — stop instead
description: When an authoritative source (Holded, a live list, a mirror) can't be read, stop or report unavailable; never fall back to a stored copy or guess that may be wrong.
---

When the source of truth for a value can't be read, the process stops (or the report says it is unavailable). Never substitute a cached copy, a stored snapshot or a default that may be wrong.

**Why:** Peter, 2026-11-03: "falling back to wrong info is worse than not running." A recon that silently used a binding's stored account number when the Holded contact list was unreadable would judge members against the wrong account and look healthy while doing it. A stopped process is visible; wrong output is not.

**How to apply:**

- A read that fails, or returns the "unavailable" sentinel (e.g. an empty list from a `...OrEmpty` helper), ends the operation with an explicit unavailable/error state and a log line.
- Don't write `primary ?? storedCopy` for data that has an authority elsewhere. A stored copy may be shown as "last known", labelled as such, but never used to decide anything.
- Don't add a fallback to quiet a reviewer or make a red light go away; that is the [`peters-working-rules`](../../docs/architecture/peters-working-rules.md) "fix at the source" rule.
