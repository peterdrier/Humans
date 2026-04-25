---
name: freshness-sweep
description: "Refresh drift-prone documentation against current code. Reads docs/architecture/freshness-catalog.yml, computes diff against the last sweep's upstream/main anchor, regenerates mechanical entries in place, processes editorial markers, and opens one PR per sweep with a report file committed alongside changes."
argument-hint: "[--full] [--interactive] [--since <ref>] [--scope <pattern>]"
---

# Freshness Sweep

Refresh drift-prone documentation files against current code. See
`docs/superpowers/specs/2026-04-25-freshness-sweep-design.md` for full design
rationale.

## Invocation

```
/freshness-sweep                     # default: diff mode, batch
/freshness-sweep --full              # weekly full-scan
/freshness-sweep --interactive       # stop at every question
/freshness-sweep --since <ref>       # override anchor for debugging
/freshness-sweep --scope <pattern>   # only run entries whose id matches glob
```

## Mode flags

- `--full`: skip anchor resolution and diff matching; every catalog entry is dirty.
- `--interactive`: stop and ask Peter at each question; default is batch (questions accumulate in the report).
- `--since <ref>`: override the auto-resolved anchor with an explicit git ref. Useful for re-processing a known range.
- `--scope <glob>`: only process entries whose `id` matches the glob (e.g., `--scope 'about-*'`).

## Phase 1: Resolve baseline

1. Run `git fetch upstream main` (always, regardless of mode). If `upstream` remote is missing, error out with: "freshness-sweep requires an `upstream` remote pointing to nobodies-collective/Humans. Configure with: `git remote add upstream https://github.com/nobodies-collective/Humans.git`".
2. If `--full` mode: skip steps 3-4. The new anchor for the commit message will be `upstream/main` HEAD; jump to Phase 2.
3. Resolve last anchor: `git log upstream/main --grep='freshness sweep' --format=%H -n 1`. Then read its commit message: `git log -1 <hash> --format=%B`. Extract the `(upstream@<sha>)` token from the message — that is the **previous anchor**.
4. If no prior sweep commit found on upstream/main: warn ("No prior freshness sweep on upstream/main — falling back to full-scan") and behave as `--full`.

The new anchor — recorded in this run's commit message — is always `upstream/main` HEAD as of the fetch in step 1.

## Phase 2: Create worktree

1. Generate timestamp: `TS=$(date -u +%Y-%m-%dT%H%M%SZ)`.
2. Run `git worktree add .worktrees/freshness-sweep-$TS -b freshness-sweep/$TS origin/main`.
3. `cd .worktrees/freshness-sweep-$TS`. All subsequent commands run inside the worktree.

If the worktree path already exists or the branch name collides, error out and instruct the user to clean up old worktrees with `git worktree list` and `git worktree remove`.

## Phase 3: Discover entries

1. Read `docs/architecture/freshness-catalog.yml` with the `Read` tool.
2. Validate schema:
   - `version` must equal `1`.
   - `mechanical`, `editorial_trees`, `ignore` must each be lists.
   - Every mechanical entry must have `id`, `target`, `triggers` (list), and either `update: script` with `script` field OR `update: prompt` with `prompt` (or `prompt-file`) field.
   - All `id` values must be unique within `mechanical`.
3. Walk `editorial_trees`. For each entry:
   - If it ends in `/`, recursively glob `**/*.md` under that path.
   - If it is a single file path, include just that file.
   - Filter out paths matching any `ignore` glob.
4. For each editorial `.md`, read the file and parse inline markers:
   - `<!-- freshness:triggers ...path patterns... -->` (one per doc, before the `# H1`)
   - `<!-- freshness:auto id="..." prompt="..." -->` … `<!-- /freshness:auto -->` (any number, must have closing tag)
   - `<!-- freshness:flag-on-change \n  reason \n -->` (any number)

   Validation:
   - Each `freshness:auto` must have a closing tag.
   - `id` attributes within a single doc must be unique.
   - `prompt` and `prompt-file` are mutually exclusive.
5. Build the unified entry list: every mechanical entry plus every editorial doc that has triggers (explicit or via flag-on-change) is a candidate. Editorial docs in `editorial_trees` with no markers at all are still candidates but treated as "unmarked editorial — flag on any src/ change".

If validation fails at any point, abort the run and print the specific error. Do not create commits or PRs.

## Phase 4: Match dirty entries

1. If `--full` mode or fallback: skip this phase. Every candidate is dirty.
2. Otherwise, get the diff path list: `git diff --name-only <previous-anchor>..upstream/main`.
3. For each candidate entry, glob-match the changed paths against its triggers:
   - Mechanical entry: triggers from `triggers` field.
   - Editorial entry with `freshness:triggers`: those globs.
   - Editorial entry without `freshness:triggers` (unmarked): trigger is `src/**` (broad catch-all, with a warning emitted to encourage the doc owner to add proper markers).
4. The dirty list is every candidate where at least one trigger matched at least one changed path.

If `--scope <glob>` was passed, filter dirty list to entries whose `id` matches.

If dirty list is empty: print "No entries dirty — nothing to refresh." and proceed to Phase 8 (cleanup).

## Phase 5: Dispatch updates

Run dispatched updates in batches of ≤3 concurrent subagents (the global hard cap from `~/.claude-shared/shared/claude.md`). Within each batch:

1. **Mechanical script-driven entries**: do NOT dispatch a subagent. Run the script directly via `Bash`. After the script completes, run `git status --short` to discover changed files. If the script touches files outside `target`, log a warning but accept all changes.

2. **Mechanical prompt-driven entries**: dispatch one subagent per entry. The subagent prompt template is:

   ```
   You are inside a worktree at <worktree-path>. Do not commit anything yourself; the parent skill handles commits.

   Your task: regenerate the file <target> per the prompt below. The trigger paths that fired are:
   <list of changed source files>

   <prompt content from the catalog entry>

   When done, return a single JSON object with this shape:
   {
     "id": "<entry id>",
     "updated": true | false,
     "files_changed": ["<paths edited>"],
     "flags": [],
     "questions": []
   }
   ```

3. **Editorial entries with `freshness:auto` blocks**: dispatch one subagent. The subagent's task is to regenerate every `freshness:auto` block in the doc per its inline `prompt`/`prompt-file`, leaving everything outside the markers untouched. Same JSON return shape.

4. **Editorial entries with `freshness:flag-on-change`** (no auto blocks): no subagent needed. The skill itself adds an entry to the flag list with the reason text from the marker.

5. **Unmarked editorial entries**: no subagent. Add an entry to the flag list with reason "Unmarked editorial doc; review for drift against changed source files: <list>".

After each batch completes, the skill reads each subagent's returned JSON, accumulates `files_changed`, `flags`, and `questions` into the run's aggregate, and proceeds to the next batch.

If `--interactive` mode and any `questions` are non-empty after a batch: stop, ask Peter inline, incorporate the answer (which may mean re-dispatching the entry), continue.

## Phase 6: Aggregate and write report

1. Compose `docs/freshness/last-report.md` with this structure:

   ```markdown
   # Freshness Sweep Report — YYYY-MM-DD HH:MM:SS UTC

   **Anchor:** upstream/main @ <sha> (previous: <prev-sha>)
   **Mode:** diff | full
   **Entries dirty:** N
   **Entries updated:** N
   **Entries flagged:** N
   **Questions accumulated:** N

   ## Updated automatically

   - `<entry id>` — <one-line summary>
   - ...

   ## Flagged for human review

   ### <file path>
   **Triggers fired:** <list of changed source files>
   **Why:** <reason from flag-on-change marker, or "unmarked editorial">
   **Suggested follow-up:** <if subagent provided one, else blank>

   ## Questions

   - (`<entry id>`) <question text>

   ## Skipped (errors)

   - (`<entry id>`) <error text>
   ```

2. Write to `docs/freshness/last-report.md` (overwrite).

3. If no `files_changed` from any subagent AND no script touched any files: do not commit, do not push, do not open a PR. Print "Nothing to update — exiting clean." Skip to Phase 8.

4. Otherwise: stage all changed files plus `docs/freshness/last-report.md`.

## Phase 7: Commit, push, open PR

1. Build the commit message:

   ```
   docs: freshness sweep — N entries (upstream@<new-anchor-sha>)

   Updated:
   - <entry id 1>
   - <entry id 2>
   - ...

   Flagged for review (see docs/freshness/last-report.md):
   - <file 1>
   - <file 2>
   - ...

   Mode: diff | full
   Previous anchor: <prev-sha>
   ```

   The `(upstream@<sha>)` token in the title MUST be present and parseable — the next sweep relies on it to resolve the prior anchor.

2. Commit: `git commit -m "$(cat <<'EOF'\n<message>\nEOF\n)"`.

3. Push: `git push -u origin freshness-sweep/$TS`.

4. Open PR:

   ```bash
   gh pr create --repo peterdrier/Humans --base main \
     --title "docs: freshness sweep — N entries (upstream@<sha>)" \
     --body "$(cat <<'EOF'
   ## Summary
   <bullets summarizing report>

   ## Report
   See `docs/freshness/last-report.md` (committed in this PR).

   ## Test plan
   - [ ] Skim the diff
   - [ ] Read the report
   - [ ] Verify flagged items in their original docs
   - [ ] Merge if happy

   🤖 Generated with [Claude Code](https://claude.com/claude-code)
   EOF
   )"
   ```

5. Print the PR URL.

## Phase 8: Tear down worktree

1. `cd ..` back to the main checkout's parent (or anywhere outside the worktree).
2. Run `git worktree remove .worktrees/freshness-sweep-$TS`. If it fails because of uncommitted state (shouldn't happen if Phase 7 succeeded, but possible if Phase 7 errored): use `git worktree remove --force` (per Peter's memory: never `rm -rf` for worktree cleanup).
3. The branch `freshness-sweep/$TS` stays on origin until the PR is merged or closed (GitHub handles branch lifecycle).

## Failure modes

| Mode | Behavior |
|---|---|
| No `upstream` remote | Error in Phase 1; nothing else runs |
| Catalog YAML parse error | Error in Phase 3; nothing else runs |
| Marker validation error in some doc | That doc is added to "Skipped (errors)" in the report; other entries continue |
| Subagent fails (returns malformed JSON or errors) | That entry is skipped; other entries continue; failure recorded in "Skipped (errors)" |
| Push fails (network, auth) | Worktree retained; user fixes credentials and re-runs Phase 7 manually |
| `gh pr create` fails | Worktree retained; report and commit are on the branch; user opens PR manually |

## What this skill does NOT do

- Schedule itself. Cadence is manual (likely daily for diff, weekly for full).
- Merge PRs. CI runs and the merge decision are Peter's.
- Touch files outside the catalog or editorial trees.
- Run if there are uncommitted changes anywhere outside the worktree (worktree is isolated; main checkout is irrelevant).
- Update `docs/architecture/maintenance-log.md` (hand-maintained for non-automated cadences).
