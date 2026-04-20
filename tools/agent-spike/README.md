# Agent Section Phase 0 Prototype

Throwaway prototype for issue [#526](https://github.com/nobodies-collective/Humans/issues/526). Validates feasibility of the in-app AI helper agent described in [`docs/superpowers/specs/2026-04-20-agent-section-design.md`](../../docs/superpowers/specs/2026-04-20-agent-section-design.md).

## What it does

1. Builds a preload corpus from the repo: all `docs/sections/*.md`, all `docs/features/*.md`, and the raw `AccessMatrixDefinitions.cs` / `SectionHelpContent.cs` files.
2. Runs each of the 20 curated questions in [`questions.yaml`](questions.yaml) against **Claude Sonnet 4.6** and **Claude Haiku 4.5**, with Anthropic prompt caching enabled for the corpus.
3. Writes per-question markdown transcripts and a cost/latency summary to `transcripts/`.

Results inform the go / no-go recommendation at [`docs/superpowers/specs/2026-04-20-agent-section-prototype-notes.md`](../../docs/superpowers/specs/2026-04-20-agent-section-prototype-notes.md).

## Running it (cmd.exe)

```cmd
cd /d H:\source\Humans\.worktrees\agent-design-526\tools\agent-spike
set ANTHROPIC_API_KEY=sk-ant-...
pip install -r requirements.txt
python run.py
```

Expected total API spend: **~$4** (20 questions × 2 models, full-corpus preload with caching).

## Outputs

| File | Purpose |
|------|---------|
| `transcripts/<model>_<question_id>.md` | One markdown file per (model, question) pair — for side-by-side review |
| `transcripts/summary.jsonl` | Machine-readable record of every turn (tokens, cost, latency, answer) |
| `transcripts/cost_report.md` | Per-model totals and averages |

## Notes

- Everything under `transcripts/` is gitignored by default (see `.gitignore`). Commit selectively if you want specific transcripts on the record.
- The spike preloads the **entire** corpus to validate the quality ceiling. The production design uses a hybrid preload + dynamic-fetch architecture; the spike demonstrates that even the most-expensive configuration is affordable.
- Haiku 4.5 uses the dated model id `claude-haiku-4-5-20251001` per Anthropic's current naming.
- Corpus is assembled at run time from the worktree — re-run after spec/feature doc edits to see the effect.
