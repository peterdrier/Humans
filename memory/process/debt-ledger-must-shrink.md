---
name: The debt ledger must shrink
description: Debt ledgers are a holding bucket, not an archive — debt runs work existing rows first and must end with fewer open rows; rows that truly can't be fixed autonomously go to GitHub issues, at most three a night.
---

The debt ledgers (central `inbox:` plus every section `Docs/debt.yml`) are a holding bucket. If the open-row count does not fall over time, the debt process has failed.

**Why:** Peter, 2026-10-04, after the nightly sweep on peterdrier/Humans#1903 added 23 rows and closed one: "if it doesn't shrink over time it has failed." Fixing what a ledger row describes is a fix — the most valuable kind.

**How to apply:**
- Debt runs work existing rows before hunting new debt. Fixing a row's code and deleting the row is a substantive fix; deleting a stale row is hygiene.
- A new row is a cost: fix what you find when you safely can, and add a row only when the run's closures already outnumber its additions.
- A row that truly can't be fixed autonomously (approval, new public surface, policy) is elevated to a `peterdrier/Humans` issue and its row deleted. Cap: **three new issues per night**, existing issues cited before filing; the rest wait in the ledger. Never bulk-file.
- The nightly runner (`.codex/cron/run-daily-debt.sh`) counts open rows before and after, and publishes a run that didn't shrink the ledger as a draft marked failed. `/debt-review` does the elevation.
