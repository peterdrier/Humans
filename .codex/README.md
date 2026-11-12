# Codex Maintenance Automation

Project-local automation for unattended Codex maintenance.

- `cron/` — the nightly debt runner (`run-daily-debt.sh`, systemd timer, spend report). Runs against a
  dedicated throwaway clone, gates on build and test, and opens one PR per night or does nothing.
  Setup and details: [`cron/README.md`](cron/README.md).
- `prompts/daily-debt.md`, `prompts/debt-ladder.md` — the nightly runner's prompts.
- `skills/humans-refactor/` — Reforge-guided single-section refactor loop.

The nightly PR is reviewed by debt review (`docs/maintenance/debt-review/README.md`).
