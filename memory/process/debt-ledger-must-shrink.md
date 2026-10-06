---
name: The debt ledger must shrink
description: Debt ledgers are a holding bucket, not an archive — debt runs work already-found rows before hunting new debt, and the ledger must shrink over time; rows that truly can't be fixed autonomously go to GitHub issues after a re-check, at most three a night.
---

The debt ledgers (central `inbox:` plus every section `Docs/debt.yml`) are a holding bucket. If the open-row count does not fall over time, the debt process has failed.

**Why:** Peter, 2026-10-04, after the nightly sweep on peterdrier/Humans#1903 added 23 rows and closed one: "if it doesn't shrink over time it has failed." Fixing what a ledger row describes is a fix — the most valuable kind. The intent is priority — "prioritize issues we've already found which need fixing over blindly searching for easier things to fix" — not discarding work: a run that didn't shrink the ledger still publishes its fixes.

**How to apply:**
- Debt runs work existing rows before hunting new, easier things to fix. Fixing a row's code and deleting the row is a substantive fix; deleting a stale row is hygiene.
- A new row is a cost: fix what you find when you safely can. Record what you can't, so no found work is wasted.
- A row that truly can't be fixed autonomously (approval, new public surface, policy) is elevated to a `peterdrier/Humans` issue and its row deleted — but only after a second agent re-checks it against the code: what Codex couldn't fix, Claude may, and a fixable or stale row is fixed or deleted, not elevated. Cap: **three new issues per night**, existing issues cited before filing; the rest wait in the ledger. Never bulk-file.
- The nightly runner (`.codex/cron/run-daily-debt.sh`) reports open rows before and after in the PR body. `/debt-review` does the elevation.
