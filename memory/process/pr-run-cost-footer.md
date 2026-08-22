---
name: Unattended runs end their PR body with the session-cost footer
description: An autonomous/routine-driven run that opens a PR appends `.claude/session-cost.py` output as the last section of the body. Cloud sessions record no dollar figure, so subagent spend is otherwise invisible.
---

**An unattended run that opens a PR** (a scheduled Routine, `/section-doctor`, any autonomous
sweep) ends its PR body with the **`## Run cost`** block emitted by `.claude/session-cost.py`.

**Why:** Claude Code on the web records no dollar figure anywhere — the `total_cost_usd` /
`estimated_usd` fields the local CLI writes for its statusline do not exist in a cloud session
(verified 2026-08-22: nothing under `~/.claude/`, no env var, nothing in the debug log). What the
transcripts *do* record is per-turn token usage plus the model, for the main loop **and** for each
subagent under `~/.claude/projects/<slug>/<session>/subagents/`. Without the footer, a run that
fans out to a dozen subagents reports no cost at all, and the fan-out is exactly where the money
goes — a one-word Explore subagent still costs ~$0.17 in cache writes on its system prompt.

**How to apply:**

- Run `.claude/session-cost.py` after the push, before `gh pr create`, and append its stdout as the
  final section of the body. `--json` gives the same numbers machine-readably.
- It never exits non-zero and never raises — a missing transcript prints one italic line and the
  PR still opens. Do not guard it with `|| true` or swallow its output.
- Do not hand-edit the numbers, and keep the "estimate, not a billed amount" disclaimer: it prices
  recorded tokens at list API rates, which is notional under subscription billing.
- Run it **inside** the session being measured, before teardown — it reads the live transcript, and
  the cloud container is reclaimed once the run ends.
- When rates or models change, update `PRICES` / `FAST_PRICES` in the script. Cache multipliers are
  1.25x input for a 5-minute write, **2.0x for a 1-hour write**, 0.1x for a read; long sessions run
  on the 1h TTL, so collapsing the two write buckets materially understates the total.
