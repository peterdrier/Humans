---
name: debt-sweep
description: "Autonomous themed tech-debt cleanup. Reads docs/architecture/debt-ledger.yml, rotates to the least-recently-served debt theme, works it for a time budget (default 2h) or until drained, opens one PR, and asks judgment questions inline at the end. Use for daily debt burndown without Peter pointing at a target: grandfathered analyzer rules, architecture-test baselines, obsolete-field reads, cross-section stitching."
argument-hint: "[--budget=2h] [--theme=<id>] [--inventory]"
---

# Debt Sweep

See `docs/superpowers/specs/2026-06-12-debt-sweep-design.md` for full design.

## Invocation

| Flag | Behavior |
|---|---|
| *(none)* | rotation picks the theme; ~2h budget |
| `--budget=<duration>` | override budget (`30m`, `4h`). Wall-clock guidance, checked **between** items, never mid-item |
| `--theme=<id>` | skip rotation, work this theme |
| `--inventory` | full re-scan: re-run every theme's detect, merge new classes, refresh counts, evict gone entries. Combinable with a normal sweep or standalone (standalone still PRs the ledger update) |

## Phase 0: Setup

`REPO_ROOT=$(git rev-parse --show-toplevel)`. Parse `--budget` (default 2h); record start time.

## Phase 1: Worktree

```bash
git fetch origin main
TS=$(date -u +%Y-%m-%dT%H%M%SZ)
git worktree add $REPO_ROOT/.worktrees/debt-sweep-$TS -b debt-sweep/$TS origin/main
WORKTREE=$REPO_ROOT/.worktrees/debt-sweep-$TS  # cd here; all commands run inside
```

**Scope is frozen here.** Never re-fetch, re-resolve, or reconcile against `origin/main` mid-run — a parallel session merging a PR while the sweep runs is expected and irrelevant. Anything landing after the branch point is the *next* sweep's input. Path/branch collision → error; instruct `git worktree list` / `git worktree remove`.

Scope every Glob/Grep to `$WORKTREE` paths — never the repo root (it holds other worktrees).

## Phase 2: Ledger + staleness check

1. Read `docs/architecture/debt-ledger.yml`. Validate: `version: 1`; every theme has `id`, `title`, `detect`, `review` (`light`|`panel`; the `inbox` theme alone uses `per-item` — each inbox entry carries its own tier), `last_swept`, `remaining`. Parse error → abort (no partial run), report to Peter.
2. **Staleness check (every run, cheap):**
   - Distinct `HUM####` ids in real `[Grandfathered(` attribute usages vs. ledger theme ids → each missing rule becomes a new theme (`last_swept: never`, `review: light` unless it is plainly a structural/judgment rule — then `panel`). **Anchor the grep to attribute syntax** — an unanchored `[Grandfathered(` also matches analyzer doc-comments and message strings, which seeds phantom themes:
     ```bash
     grep -rn -A3 --include='*.cs' '^[[:space:]]*\[Grandfathered(' $WORKTREE/src | grep -oE '"HUM[0-9]{4}"' | sort -u
     ```
   - Files in `tests/Humans.Application.Tests/Architecture/Baselines/` with >0 non-comment entries vs. ledger → same.
   - New themes are committed in the ledger update (Phase 5) even when not worked this run.
3. **`--inventory` only:** re-run every theme's `detect`, refresh all `remaining`, evict themes whose debt is gone (note each eviction in the report), and look for debt classes the ledger misses (new `[Obsolete]` clusters, new custom warning ids in a fresh build, new ratchet analyzers).

### Build-derived counts

Warning-backed themes (`detect: build:<CODE>`) are counted from one build log instead of per-theme commands:

```bash
dotnet build Humans.slnx -v quiet --no-incremental 2>&1 | tee /tmp/debt-sweep-build.log >/dev/null
grep -E "warning <CODE>" /tmp/debt-sweep-build.log | sed 's/ \[.*//' | sort -u | wc -l   # distinct sites
```

Run this build once in Phase 3 (it doubles as the baseline build) and reuse the log for every `build:` detect this run.

## Phase 3: Pick theme

1. `--theme` if given; else: order themes by `last_swept` ascending (`never` first), skip `remaining: 0`.
2. Run the candidate's `detect` to confirm `remaining > 0`. If 0 → apply the drain rule (below), update the entry, take the next candidate.
3. Enumerate the theme's concrete items (file list from grep, baseline lines, distinct warning sites). Order items so anything in a `recent_sections` section is worked **last**.
4. Fold in `inbox` items that match the chosen theme.

**Drain rule:** when a theme hits 0 and a structural enforcer guards regression (analyzer at Error with no remaining grandfathers; architecture test whose baseline is empty), **retire** the entry — delete it from the ledger and note it in the report. The Phase 2 staleness check re-creates it if the debt ever reappears. Themes without an enforcer stay listed at `remaining: 0` and are only re-checked by `--inventory`.

## Phase 4: Work loop

Until budget exhausted or theme drained, per item (one item or one tight cluster per commit):

1. **Fix it right** — no surgical fixes (constitution). Reuse-first; match existing patterns (the theme's `notes` often names the reference implementation).
2. `dotnet build Humans.slnx -v quiet`.
3. Targeted tests: the touched section's tests + `--filter` the Architecture tests. Full `dotnet test Humans.slnx -v quiet` at minimum before each push.
4. **EF model-drift gate** (themes touching entities, navs, or `Data/Configurations`): the sweep never creates migrations, so verify the fix didn't silently change the model — `cd src/Humans.Infrastructure && dotnet ef migrations has-pending-model-changes`; pending changes → revert the item, classify it as schema work, record it (Phase 7 question or inbox).
5. **Forbidden-move grep** on the item diff: `#pragma warning disable HUM`, `[SuppressMessage`, *new* `[Grandfathered]`, `// ReSharper disable`, visibility narrowing that dodges a rule rather than fixes it. Any hit → revert the item, record in report.
6. **`review: panel` themes only:** dispatch a second-opinion reviewer subagent — opus-tier, read-only, score-blind, default-reject (refactor-swarm posture): "is this fix a good idea, not merely green — name the concept that improved in one sentence." Name it `debt-review-<item>-opus`, description tagged `(opus)`. Reject → rework once; persistent reject → revert, skip item, record.
7. Commit with a one-line `debt(<theme-id>): <what>` message. Push every 3–5 items.

Rules of the loop:

- **Stop-and-ask classes are skip-and-ask classes here:** interface/public-surface additions (`interface-method-additions-are-debt`), DB schema work of any kind, privilege changes → skip the item, queue a Phase 7 question. Never block the loop waiting.
- Off-theme debt discovered while working → append to ledger `inbox`, never chased.
- Mechanical edit fan-out is allowed via edit-only subagent workers (sonnet, named `<task>-sonnet`, absolute `$WORKTREE` paths, no git/build); the orchestrator owns all git and build commands.
- An item that can't be made green after a genuine attempt → revert it cleanly, record, continue. Never leave the branch red between commits.

## Phase 5: Ledger update + report

In the worktree:

1. Theme entry: `last_swept: <today>`, `remaining:` from re-running `detect`, apply drain/retire rule.
2. `recent_sections:` ← dominant sections of the last ~3 sweeps (this one included).
3. Add Phase 2 staleness discoveries; append work-loop inbox items; remove inbox items completed this run; apply `--inventory` evictions.
4. Overwrite `docs/debt/last-report.md`: timestamp, theme, budget used; items fixed (one line each: what + commit sha); items skipped + why (schema, interface-addition, panel-reject, budget); forbidden-move reverts; inbox additions; ledger changes (new themes, retirements, evictions).

Commit ledger + report with the work (same PR).

## Phase 6: PR

```bash
git push -u origin debt-sweep/$TS
gh pr create --repo peterdrier/Humans --base main \
  --title "debt: <theme title> — N items" \
  --body "$(cat <<'EOF'
## Theme
<theme id — one-line description>

## Fixed
<per-item bullets>

## Skipped
<item — reason>

## Ledger
<delta: counts, new themes, retirements>

Report: `docs/debt/last-report.md` (committed in this PR).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

One PR per sweep. Print the PR URL.

## Phase 7: Resolve review items inline (no homework)

After the PR is open and **before** teardown, present **every item that needs Peter's judgment inline in chat** — terse, numbered, one line each, answerable in a word or two: skipped schema/interface items, panel rejects worth debating, uncertain classifications, anything queued during the loop. The report is the record, not the delivery channel — assume Peter never opens it. Inline prose, never the `AskUserQuestion` tool (project rule).

Wait for answers. Apply resulting edits in the worktree, **`git add` + new `git commit`** (a bare push after editing sends nothing), update the report's skipped/questions sections with each resolution, `git push`.

Zero judgment calls → say so in one line and proceed.

## Phase 8: Teardown

Only after Phase 7 resolves: `cd $REPO_ROOT && git worktree remove $WORKTREE` (`--force` only if Phase 6 errored). Never `rm -rf`. Branch stays on origin until the PR closes.

## Adding debt to the ledger (any session, any time)

- **Recurring class** → append a `themes:` entry with `last_swept: never` (rotation serves it next automatically). Pick `review:` honestly: `light` only when the fix is rule-prescribed and the verifier is mechanical.
- **One-off item** → append to `inbox:` with `added: <date>` and a one-line `what:`.
- Ledger-only changes ride the discovery PR or go direct to `origin/main` per `no-direct-to-main`.

## Standing constraints

- **No EF migrations, ever.** No schema changes, no snapshot edits, no `dotnet ef migrations add`. DTO/view-model/non-entity property drops are fine. The Phase 4 drift gate enforces this.
- **Never touch `[DontFix]`** — Peter-applied permanent exceptions; skip those sites entirely.
- **No analyzer suppressions** in any form (`no-analyzer-suppressions`).
- **No data migrations / backfills** (`no-data-backfills`).
- Explicit subagent models, tagged in name + description (sonnet workers, opus-tier panel reviewers).
- The sweep touches only: the theme's item files, `docs/architecture/debt-ledger.yml`, and `docs/debt/last-report.md`.
- After the run, update `docs/architecture/maintenance-log.md` per `maintenance-log-update` (separate from the sweep PR if needed).

## Failure modes

| Failure | Behavior |
|---|---|
| Ledger YAML parse error | Abort before any work; report |
| `detect` command fails | Skip theme, pick next; record |
| Item breaks build/tests, can't right it | Revert item, record, continue |
| Panel rejects twice | Revert, skip, record |
| EF drift gate fires | Revert item, classify as schema work, record |
| Budget hit mid-theme | Normal: commit what's done; ledger carries the remainder |
| Push / PR fails | Worktree retained; fix manually |
