---
name: freshness-sweep
description: "Refresh drift-prone documentation against current code. Reads docs/architecture/freshness-catalog.yml, diffs against the last sweep's upstream/main anchor, regenerates mechanical entries, processes editorial markers, and opens one PR per sweep with a report file."
argument-hint: "[--full] [--interactive] [--since <ref>] [--scope <pattern>]"
---

# Freshness Sweep

See `docs/superpowers/specs/2026-04-25-freshness-sweep-design.md` for full design.

## Invocation

| Flag | Behavior |
|---|---|
| *(none)* | diff mode, batch |
| `--full` | skip anchor resolution; every entry is dirty |
| `--interactive` | stop at each question; default accumulates in report |
| `--since <ref>` | override anchor (debugging) |
| `--scope <glob>` | only mechanical entries whose `id` matches; editorial unaffected |

## Phase 0: Capture repo root

`REPO_ROOT=$(git rev-parse --show-toplevel)` — save for Phase 8 teardown.

## Phase 1: Resolve baseline

1. `git fetch upstream main`. Missing remote → error: `git remote add upstream https://github.com/nobodies-collective/Humans.git`.
2. `--since <ref>`: `previous-anchor = git rev-parse <ref>`. Skip 3-5.
3. `--full`: previous-anchor = `none`; new anchor = `upstream/main` HEAD → Phase 2.
4. `git log upstream/main --grep='(upstream@' --extended-regexp --format=%H -n 1` → `git log -1 <hash> --format=%B` → extract `(upstream@<sha>)` token. Grep on the anchor token (not "freshness sweep") to avoid false positives from revert commits.
5. No prior sweep: warn, previous-anchor = `none`, behave as `--full`.

## Phase 2: Create worktree

```bash
TS=$(date -u +%Y-%m-%dT%H%M%SZ)
git worktree add $REPO_ROOT/.worktrees/freshness-sweep-$TS -b freshness-sweep/$TS origin/main
WORKTREE=$REPO_ROOT/.worktrees/freshness-sweep-$TS  # cd here; all commands run inside
```

Path/branch collision: error; instruct `git worktree list` / `git worktree remove`.

## Phase 3: Discover entries

1. Read `docs/architecture/freshness-catalog.yml`. Validate: `version=1`; `mechanical`, `editorial_trees`, `ignore` are lists; every mechanical entry has `id`, `target`, `triggers[]`, and either `update: script`+`script` or `update: prompt`+`prompt`/`prompt-file`; `id` and `target` unique within `mechanical`. Warn (don't fail) on trigger glob matching no files.
2. Walk `editorial_trees`: paths ending in `/` → glob `**/*.md`; single paths included directly. Filter by `ignore` globs.
3. Parse inline markers per editorial `.md`:
   - `<!-- freshness:triggers ...globs... -->` (one per doc, before `# H1`)
   - `<!-- freshness:auto id="..." prompt="..." -->` … `<!-- /freshness:auto -->` (closing tag required; `id` unique per doc; `prompt`/`prompt-file` mutually exclusive)
   - `<!-- freshness:flag-on-change` … reason … `-->` (multi-line comment)
4. Unified entry list: all mechanical + editorial docs with any marker. Docs in `editorial_trees` with no markers are "unmarked editorial" candidates.

Abort on validation failure → Phase 8.

## Phase 4: Match dirty entries

1. `--full`/fallback: every candidate dirty.
2. `git diff --name-only <previous-anchor>..upstream/main` → glob-match against each entry's triggers.
3. `--scope <glob>`: filter to mechanical entries whose `id` matches.
4. Empty dirty list → "No entries dirty — nothing to refresh." → Phase 8.

## Phase 5: Dispatch updates (≤3 concurrent subagents)

| Entry type | Slot | Action |
|---|---|---|
| Mechanical `update: script` | none | Run via `Bash`; warn if files touched outside `target` |
| Mechanical `update: prompt` | 1 | Dispatch subagent |
| Editorial `freshness:auto` | 1 | Dispatch subagent; regenerate each block per inline prompt; leave content outside markers untouched |
| Editorial `freshness:flag-on-change` | none | Add to flag list with reason from marker |
| Unmarked editorial | none | Add to flag list: "Unmarked editorial; review for drift: \<files\>" |

Subagent prompt: worktree path, target file, trigger paths fired, entry's prompt content. Must NOT commit. Return JSON:

```json
{
  "id": "<entry id>",
  "updated": true,
  "files_changed": ["<paths>"],
  "summary": "<one-line; required when updated>",
  "flags": [{ "file": "<path>", "reason": "<why>", "suggested_follow_up": "<optional>" }],
  "questions": ["<text>"]
}
```

After each batch accumulate results. `--interactive`: stop on non-empty `questions`, ask Peter, continue.

## Phase 5.5: Prune (~5% of doc lines per sweep)

Goal: every sweep removes ~5% of the documentation pile so it doesn't grow unbounded. Soft target 5%, hard cap 7%.

Dispatch ONE subagent (separate slot from Phase 5; runs after Phase 5 completes so it can see regenerated targets). The agent operates under a strict allowlist — it is **never** allowed to touch:

- anything outside `docs/`
- `docs/architecture/freshness-catalog.yml`
- `docs/architecture/design-rules.md`, `docs/architecture/code-review-rules.md`, `docs/architecture/coding-rules.md`, `docs/architecture/conventions.md`
- `docs/sections/`, `docs/features/`, `docs/guide/` (these are the living spec; editorial markers handle them)
- `docs/freshness/last-report.md`
- the entry-index `freshness:auto` block in `docs/architecture/data-model.md` or the suppressions block in `docs/architecture/code-analysis.md` (those are regenerated by Phase 5)

It IS allowed to delete or reduce, with evidence cited per item:

| Candidate | Allowed action | Required evidence |
|---|---|---|
| `docs/plans/*.md` older than 30 days | Delete whole file | Feature/PR shipped (link commit or PR), or plan explicitly "DONE"/"superseded" |
| `docs/superpowers/plans/*.md` older than 30 days | Delete whole file | Same — named feature lands on `upstream/main` |
| `docs/superpowers/specs/*.md` older than 60 days | Delete whole file | Section doc in `docs/sections/` now covers the same scope |
| `docs/architecture/tech-debt-*.md` where all items marked `[DONE]` | Delete | Inline `[DONE]` markers exhaustive |
| Orphan refs to deleted plans/specs from living docs | Edit out the dead link | Target file no longer exists |

Calculation: `total_doc_lines = wc -l docs/**/*.md` at start of phase. Target deletion = `total_doc_lines * 0.05`; hard cap = `total_doc_lines * 0.07`. Stop adding deletions once the running total reaches the cap.

Subagent prompt MUST require: per-item JSON entry with `file`, `lines_removed`, `evidence` (link or quoted text), `confidence` (`high`|`medium`). Only `high`-confidence items are applied. `medium`-confidence items are added to the report's flag list for Peter to action manually.

Subagent return:

```json
{
  "phase": "prune",
  "total_lines_before": 142482,
  "target_lines": 7124,
  "applied": [
    { "file": "docs/plans/foo.md", "lines_removed": 187, "evidence": "Closes peterdrier/Humans#123 merged 2026-04-12", "confidence": "high" }
  ],
  "proposed_for_review": [
    { "file": "docs/superpowers/specs/bar.md", "lines_removed_if_applied": 412, "evidence": "Section doc Tickets.md covers same scope", "confidence": "medium" }
  ],
  "applied_total_lines": 6420,
  "questions": []
}
```

Orchestrator stages the applied deletions, adds the proposed-for-review list to the report.

## Phase 6: Aggregate and write report

Overwrite `docs/freshness/last-report.md`: timestamp header; anchor/mode/counts summary; "Updated automatically" bullets (`id — summary`); "Pruned" section (file, lines removed, evidence) from Phase 5.5; "Flagged for human review" (file, triggers, reason, follow-up); medium-confidence prune candidates ("Proposed for prune — review"); "Questions"; "Skipped (errors)". Previous-anchor = `none` on first run or `--full`. No files changed → "Nothing to update." → Phase 8. Otherwise stage all changes + report.

## Phase 7: Commit, push, open PR

Commit title MUST be `docs: freshness sweep — N entries (upstream@<new-anchor-sha>)`. The `(upstream@<sha>)` token is parsed by the next sweep to locate the prior anchor.

```bash
git commit -m "$(cat <<'EOF'
docs: freshness sweep — N entries (upstream@<sha>)

Updated:
- <entry-id>

Flagged for review (see docs/freshness/last-report.md):
- <file>

Mode: diff
Previous anchor: <sha-or-none>

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

**Closing `EOF` must be at column 0.**

```bash
git push -u origin freshness-sweep/$TS
gh pr create --repo peterdrier/Humans --base main \
  --title "docs: freshness sweep — N entries (upstream@<sha>)" \
  --body "$(cat <<'EOF'
## Summary
<bullets from report>

## Report
See `docs/freshness/last-report.md` (committed in this PR).

## Test plan
- [ ] Skim diff, read report, verify flagged items, merge if happy

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Print the PR URL.

## Phase 8: Tear down worktree

`cd $REPO_ROOT && git worktree remove $WORKTREE` (add `--force` if Phase 7 errored). Never `rm -rf`. Branch stays on origin until PR closes.

## Failure modes

| Failure | Behavior |
|---|---|
| No `upstream` remote | Error in Phase 1 |
| Catalog YAML parse error | Error in Phase 3 → Phase 8 |
| Marker validation error | That doc → "Skipped (errors)"; others continue |
| Subagent fails | That entry skipped; recorded in "Skipped (errors)" |
| Duplicate target in catalog | Schema validation rejects at parse time |
| Trigger glob matches nothing | Warning only |
| Push / PR creation fails | Worktree retained; fix manually |

## Constraints

- Only touches files in the catalog, editorial trees, or the prune allowlist (Phase 5.5).
- Main checkout dirty state is irrelevant — all work is in the worktree.
- Does not update `docs/architecture/maintenance-log.md` (hand-maintained).
- Prune phase (5.5) never touches living architectural docs (`design-rules.md`, `code-review-rules.md`, section invariants, feature specs). It only deletes shipped/historical plans and specs with cited evidence.
