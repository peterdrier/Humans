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

**Fetch `origin/main` first, and branch off its fresh HEAD** — not a stale local ref. The worktree base is the code the mechanical regens read; if it lags `origin/main`, every regenerated doc is generated against stale source and the PR Surface Report flags phantom interface/score deltas (base-vs-head sees code the branch hasn't caught up to). This bit sweep #819 (branch cut at `35ce66de8`, `origin/main` advanced to `cdd850bde` mid-sweep, surface report showed a removed `ITicketingBudgetRepository` as "new").

```bash
git fetch origin main
TS=$(date -u +%Y-%m-%dT%H%M%SZ)
git worktree add $REPO_ROOT/.worktrees/freshness-sweep-$TS -b freshness-sweep/$TS origin/main
WORKTREE=$REPO_ROOT/.worktrees/freshness-sweep-$TS  # cd here; all commands run inside
```

If `git merge-base --is-ancestor upstream/main origin/main` is **false** — i.e. `origin/main` does not contain `upstream/main` HEAD (they've crossed, usually right after a prod promotion) — warn: the diff anchor (`upstream/main`) and the worktree code base (`origin/main`) describe different trees, so a doc regenerated here may not match what the PR diffs against. Proceed, but expect to reconcile in Phase 7 (re-fetch `origin/main` and merge it into the branch before opening the PR if the surface report shows code deltas on a docs-only sweep).

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
| Editorial `freshness:flag-on-change` | none | See below — fix concrete broken facts directly; flag only subjective prose review |
| Unmarked editorial | none | See below — same rule; also add to flag list: "Unmarked editorial; review for drift: \<files\>" |

**Flag list ≠ dumping ground.** `freshness:flag-on-change` exists for *subjective* drift — prose that a human should re-judge ("does this still read right after the area changed?"). It is **not** a way to defer **concrete broken facts**. If a triggered editorial doc names a symbol that the diff renamed or removed (a deleted interface, a moved type, a dropped column, a renamed route), that is a verifiable factual error against the code — **fix it directly in this sweep** (a tightly-scoped subagent reading the current code is fine), don't just add it to the flag list for Peter to fix by hand. Reserve the flag list for genuinely subjective calls; if you're unsure whether a flagged doc has a hard error or just needs a tone pass, grep the code to find out before deciding. The default is fix, not flag.

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

## Phase 5.5: Prune — wheat extraction, then deletion

Goal: every sweep shrinks the historical-doc pile by ~5% (soft target, ~7% soft reviewability budget — see Orchestration for how the budget interacts with fully-mined husks) **without losing durable signal**. Whole-file deletion is the LAST step, never the first. The earliest sweep that tried to delete first lost ADR rationale, vendor-selection trade-offs, and decorator-integrity gotchas — those have to be extracted into living docs before the husk is removed.

### Per-source workflow (every candidate doc goes through this)

1. **Read** the historical doc in full + read every candidate target section/architecture doc in full.
2. **Identify wheat** — durable signal: design decisions with rationale, rejected alternatives that explain why current behavior is the way it is, gotchas, negative-space rules, vendor/library selection rationale, external-system quirks.
3. **Identify chaff** — data model tables (code is the spec), implementation task lists, code samples already in src/, status/date markers, restatements of obvious behavior, glossary etymology, "we will/might do X" speculation.
3a. **Verify candidate wheat against the code — the code is the reference.** Before migrating a stated invariant/trigger/rule, confirm it against current source (grep/read the named service, entity, config). Three outcomes, no fourth:
   - **Still true** → migrate it (step 6), phrased to match the code, not the stale spec's wording.
   - **No longer true** (the intention is dead — code changed since) → it's chaff; drop it.
   - **Genuinely can't tell whether it's still a live intention worth keeping** → **ASK Peter inline now** (plain prose, one question; never the `AskUserQuestion` tool per project rule). Do **not** queue it in a "Proposed for review" list for a future pass. Queuing an uncertain item for later is the anti-pattern this step exists to kill — the whole point of the sweep is to resolve, not to generate a human to-do backlog.
4. **De-duplicate against target.** If the wheat is already in the target doc, drop it.
5. **Genre-check against EXISTING destinations only.** Allowed:
   - `docs/sections/*.md` — section invariants only, no rationale narrative (per `SECTION-TEMPLATE.md`)
   - `docs/architecture/design-rules.md` — architecture-level decisions that extend the constitution
   - `docs/architecture/conventions.md` — pattern definitions (when to use X, naming, etc.)
   - **Never** create new design docs, ADR files, or `memory/` atoms during a sweep. `memory/` is for **atomic task-fires rules**, not for narrative-history-of-decisions, and design docs carry weight that needs Peter's review. If verified-true wheat genuinely fits none of the three allowed destinations, **ASK Peter inline** where it should live (don't invent a destination, and don't silently drop durable signal). Asking is fine; queuing for "next pass" is not.
6. **Migrate** the wheat with a `<!-- wheat: <source path> §<section> -->` provenance comment. Preserve original prose voice.
7. **Scan for inbound refs** to the historical doc across all `.md` files. For each ref:
   - If from a living doc (sections, features, guide, architecture): retarget to the destination of the migrated wheat.
   - If from an archive doc (`docs/superpowers/**`, other historical docs): rewrite as `(historical) — current invariants live in <destination>`. The archive remains historical; the link points forward.
8. **Delete the husk** only after steps 6 + 7 are complete.

### Allowlist of sources

| Source tree | Action |
|---|---|
| `docs/plans/*.md` older than 30 days | Wheat-extract → migrate → retarget refs → delete |
| `docs/superpowers/plans/*.md` older than 30 days | Same |
| `docs/superpowers/specs/*.md` older than 60 days | Same |
| `docs/architecture/tech-debt-*.md` where all items are `[DONE]` | Same (wheat may be `[DONE]` summaries worth keeping in maintenance-log) |
| Orphan refs in living docs to already-deleted files | Edit out or retarget |

### Never touched by prune

- Anything outside `docs/`
- `docs/architecture/freshness-catalog.yml`
- `docs/sections/`, `docs/features/`, `docs/guide/` as deletion targets (these are migration *destinations*, never sources)
- `docs/architecture/{design-rules,code-review-rules,coding-rules,conventions}.md` as deletion targets (same — these are destinations)
- `docs/freshness/last-report.md`
- the `freshness:auto` blocks in `data-model.md` and `code-analysis.md` (Phase 5 owns those)

### Sizing

```bash
shopt -s globstar  # REQUIRED — without this, ** doesn't recurse and total_lines is wildly undercounted
total_doc_lines=$(find docs -name '*.md' -print0 | xargs -0 wc -l | tail -1 | awk '{print $1}')
target_lines=$((total_doc_lines * 5 / 100))
hard_cap=$((total_doc_lines * 7 / 100))
```

Use `find` (above) rather than relying on shell globbing — it's safe whether globstar is set or not.

### Orchestration

Dispatch up to 3 subagents in parallel (one per logical source group: e.g., shifts-related, auth-related, infra-related). Each agent:

- Takes 1–N source docs sharing a likely destination
- Reads sources + candidate targets
- Returns a JSON manifest (below) — does NOT write files

Orchestrator reviews each manifest, applies migrations as `Edit` calls, retargets inbound refs, then deletes husks. The 7% figure is a **soft reviewability budget** (keep one PR's deletions skimmable), not a correctness limit. Apply it like this:

- Stop *starting new* husk deletions once `applied_lines + this_doc_lines > hard_cap`.
- **Exception — never strand a mined husk.** If you extracted wheat from a husk *this sweep*, delete that husk *this sweep* even if it nudges you past 7%. Leaving an emptied husk for "next time" is the deferral anti-pattern; a few hundred over-cap lines of already-extracted dead weight is fine — note the overage and why in the report.
- A husk you chose not to analyze this sweep (pure budget) may be listed as a future-sweep candidate. That is a budget decision, not a punt — distinct from queuing an *uncertain* item, which is never allowed (resolve it via step 3a instead).

### Subagent return format

```json
{
  "batch": "<logical group name>",
  "migrations": [
    {
      "source_doc": "<path being mined>",
      "target_doc": "<destination living doc>",
      "insertion_anchor": "<exact existing line in target>",
      "text_to_insert": "<markdown block including <!-- wheat: ... --> comment>",
      "lines_inserted": <int>,
      "wheat_summary": "<one-line: what survived and why it's durable>"
    }
  ],
  "drop_entirely": [
    { "source_doc": "<path>", "reason": "<all chaff: data-model now in code, task lists, etc.>" }
  ],
  "inbound_refs": [
    { "source_doc": "<path>", "ref_from": "<path>", "ref_line": <int>, "proposed_action": "retarget to <dest>" | "rewrite as historical" | "remove" }
  ],
  "questions": []
}
```

### Result-section in the sweep report

The sweep report's "Pruned" section lists:
- For each migration: `<source> → <target>` + the one-line wheat summary
- For each husk deleted: file + line count + "all chaff" reason
- For each retargeted ref: source ref → new target

A prune-analysis subagent's manifest may surface **medium-confidence wheat** (the agent isn't sure it's still durable). The orchestrator does **not** pass that uncertainty through to a "Proposed for review" backlog — it resolves it per step 3a: verify against code, then migrate (if true), drop (if dead), or ask Peter inline (if genuinely a judgment call). The report's "Proposed for review" section should normally read **"None — all candidates resolved this sweep"**; it carries content only when Peter was asked a question this sweep and the answer is pending.

## Phase 6: Aggregate and write report

Overwrite `docs/freshness/last-report.md`: timestamp header; anchor/mode/counts summary; "Updated automatically" bullets (`id — summary`); "Pruned" section (file, lines removed, evidence) from Phase 5.5, including a **"Wheat migrated"** sub-list (`source §section → destination`, with the code symbol that verified it); "Flagged for human review" (file, triggers, reason — subjective prose only, never concrete broken facts, which are fixed inline); "Proposed for review" ("None — all candidates resolved this sweep" unless a question to Peter is pending); "Questions" (anything you asked Peter inline this sweep); "Skipped (errors)". Previous-anchor = `none` on first run or `--full`. No files changed → "Nothing to update." → Phase 8. Otherwise stage all changes + report.

## Phase 7: Commit, push, open PR

**First, reconcile a moved base.** Re-run `git fetch origin main`. If `origin/main` advanced past the worktree base while the sweep ran, merge it into the branch now (`git merge origin/main` — clean, since the sweep only touches docs and code lands elsewhere) so the PR diffs against current `origin/main` and the Surface Report doesn't flag phantom code deltas on a docs-only PR. If any mechanical doc was regenerated against the now-superseded code, re-run that entry against the merged tree before committing.

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
