---
name: spend
description: After-the-fact cost report for a finished or running Claude Code session — tokens and estimated dollars per agent, parsed from transcripts. Use when the user says "/spend", "what did that cost", "cost breakdown", "where did the tokens go", or at the end of an orchestrated run. Not the built-in /cost total (that's live-session only, no per-agent breakdown).
---

# spend

A script reads the transcripts; you never do. They are enormous JSONL files and
opening one directly would flood context for a report that should cost almost
nothing to produce.

## Run it

```bash
python ~/.claude/skills/spend/scripts/spend.py [<session-id> | <transcript-path>] [--log]
```

No argument: reports the most recent session for the current working directory's
project (run from the project root). Add `--log` after an orchestrated run to
append the totals to `~/.claude/spend-log.jsonl` for later trend analysis — skip
it for a plain "what did that cost" lookup.

Show the printed markdown table verbatim. Then add up to three lines of
observations, e.g.:
- orchestrator share vs. the cheapest way this could have gone
- the priciest agent, and whether its tier matches its actual cost
- anything shown as `untagged` (no tier suffix) or under "unpriced models"

Never open a transcript file directly (`Read`, `cat`, grep-and-page-through) to
answer a cost question — they can be tens of megabytes per session and dwarf any
answer they'd produce. If the script's output is insufficient, extend the script.
