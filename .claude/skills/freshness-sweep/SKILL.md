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
- `--scope <glob>`: only process entries whose `id` matches the glob (e.g., `--scope 'about-*'`). Applies to mechanical entries only — editorial entries are identified by file path, not `id`, and are not filtered by `--scope`.

## Phase 0: Capture repo root

Before doing anything else, record the absolute path of the current main checkout (the directory the user invoked `/freshness-sweep` from):

```
REPO_ROOT=$(git rev-parse --show-toplevel)
```

Save this for Phase 8 — `cd $REPO_ROOT` reliably leaves the worktree on teardown regardless of how nested the worktree path is.

## Phase 1: Resolve baseline

1. Run `git fetch upstream main` (always, regardless of mode). If `upstream` remote is missing, error out with: "freshness-sweep requires an `upstream` remote pointing to nobodies-collective/Humans. Configure with: `git remote add upstream https://github.com/nobodies-collective/Humans.git`".
2. **If `--since <ref>` was passed:** set `<previous-anchor>` to the resolved SHA of `<ref>` (`git rev-parse <ref>`). Skip steps 3-5.
3. **If `--full` mode:** skip steps 4-5. There is no previous anchor; report it as `none` in commit message and report file. The new anchor is `upstream/main` HEAD; jump to Phase 2.
4. Resolve last anchor: `git log upstream/main --grep='(upstream@' --extended-regexp --format=%H -n 1`. The grep matches the anchor *token* embedded in every prior sweep commit message — using the loose phrase "freshness sweep" would risk false positives from any later commit that mentions the phrase (e.g. "revert freshness sweep change"). Then read its commit message: `git log -1 <hash> --format=%B`. Extract the `(upstream@<sha>)` token from the message — that is the **previous anchor**.
5. If no prior sweep commit found on upstream/main: warn ("No prior freshness sweep on upstream/main — falling back to full-scan"), set `<previous-anchor>` to `none`, and behave as `--full`.

The new anchor — recorded in this run's commit message — is always `upstream/main` HEAD as of the fetch in step 1.

## Phase 2: Create worktree

1. Generate timestamp: `TS=$(date -u +%Y-%m-%dT%H%M%SZ)` (no colons; safe on Windows paths and git refs).
2. Run `git worktree add $REPO_ROOT/.worktrees/freshness-sweep-$TS -b freshness-sweep/$TS origin/main`.
3. Save absolute worktree path: `WORKTREE=$REPO_ROOT/.worktrees/freshness-sweep-$TS`.
4. `cd $WORKTREE`. All subsequent commands run inside the worktree.

If the worktree path already exists or the branch name collides, error out and instruct the user to clean up old worktrees with `git worktree list` and `git worktree remove`.

## Phase 3: Discover entries

1. Read `docs/architecture/freshness-catalog.yml` with the `Read` tool.
2. Validate schema:
   - `version` must equal `1`.
   - `mechanical`, `editorial_trees`, `ignore` must each be lists.
   - Every mechanical entry must have `id`, `target`, `triggers` (list), and either `update: script` with `script` field OR `update: prompt` with `prompt` (or `prompt-file`) field.
   - All `id` values must be unique within `mechanical`.
   - All `target` paths must be unique within `mechanical` (two entries cannot rewrite the same file).
   - For each trigger glob, warn (but do not fail) if it matches zero existing files — likely a typo or rename.
3. Walk `editorial_trees`. For each entry:
   - If it ends in `/`, recursively glob `**/*.md` under that path.
   - If it is a single file path, include just that file.
   - Filter out paths matching any `ignore` glob.
4. For each editorial `.md`, read the file and parse inline markers:
   - `<!-- freshness:triggers ...path patterns... -->` (one per doc, before the `# H1`)
   - `<!-- freshness:auto id="..." prompt="..." -->` … `<!-- /freshness:auto -->` (any number, must have closing tag)
   - `<!-- freshness:flag-on-change` (open) followed by reason text on the next line(s) followed by `-->` (close), as a multi-line HTML comment. Example:

     ```markdown
     <!-- freshness:flag-on-change
       Authorization rules — review when controllers/services in this section change.
     -->
     ```

   Validation:
   - Each `freshness:auto` must have a closing tag.
   - `id` attributes within a single doc must be unique.
   - `prompt` and `prompt-file` are mutually exclusive.
5. Build the unified entry list: every mechanical entry plus every editorial doc that has triggers (explicit or via flag-on-change) is a candidate. Editorial docs in `editorial_trees` with no markers at all are still candidates but treated as "unmarked editorial — flag on any src/ change".

If validation fails at any point, abort the run with the specific error and proceed to Phase 8 to tear down the worktree. Do not create commits or PRs.

## Phase 4: Match dirty entries

1. If `--full` mode or fallback: skip this phase. Every candidate is dirty.
2. Otherwise, get the diff path list: `git diff --name-only <previous-anchor>..upstream/main`.
3. For each candidate entry, glob-match the changed paths against its triggers:
   - Mechanical entry: triggers from `triggers` field.
   - Editorial entry with `freshness:triggers`: those globs.
   - Editorial entry without `freshness:triggers` (unmarked): trigger is `src/**` (broad catch-all, with a warning emitted to encourage the doc owner to add proper markers).
4. The dirty list is every candidate where at least one trigger matched at least one changed path.

If `--scope <glob>` was passed, filter the dirty list to mechanical entries whose `id` matches. Editorial entries are not filtered (they have no `id`).

If dirty list is empty: print "No entries dirty — nothing to refresh." and proceed to Phase 8 (cleanup).

## Phase 5: Dispatch updates

Run dispatched updates in batches of ≤3 concurrent subagents (the global hard cap from `~/.claude-shared/shared/claude.md`). Within each batch:

1. **Mechanical script-driven entries** (no concurrency slot consumed): do NOT dispatch a subagent. Run the script directly via `Bash`. After the script completes, run `git status --short` to discover changed files. If the script touches files outside `target`, log a warning but accept all changes.

2. **Mechanical prompt-driven entries** (one concurrency slot per entry): dispatch one subagent per entry. The subagent prompt template is:

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
     "summary": "<one-line description of what changed, used in the report's 'Updated automatically' bullet>",
     "flags": [
       {
         "file": "<path>",
         "reason": "<why this needs human review>",
         "suggested_follow_up": "<optional concrete next step>"
       }
     ],
     "questions": ["<question text>"]
   }

   `flags` and `questions` may be empty arrays. `summary` is required when `updated: true`.
   ```

3. **Editorial entries with `freshness:auto` blocks** (one concurrency slot per entry): dispatch one subagent. The subagent's task is to regenerate every `freshness:auto` block in the doc per its inline `prompt`/`prompt-file`, leaving everything outside the markers untouched. Same JSON return shape.

4. **Editorial entries with `freshness:flag-on-change`** (no concurrency slot consumed): no subagent needed. The skill itself adds an entry to the flag list with the reason text from the marker.

5. **Unmarked editorial entries** (no concurrency slot consumed): no subagent. Add an entry to the flag list with reason "Unmarked editorial doc; review for drift against changed source files: <list>".

Only types 2 and 3 consume the concurrency cap; types 1, 4, 5 are processed synchronously by the skill itself.

After each batch completes, the skill reads each subagent's returned JSON, accumulates `files_changed`, `summary`, `flags`, and `questions` into the run's aggregate, and proceeds to the next batch.

If `--interactive` mode and any `questions` are non-empty after a batch: stop, ask Peter inline, incorporate the answer (which may mean re-dispatching the entry), continue.

## Phase 6: Aggregate and write report

1. Compose `docs/freshness/last-report.md` with this structure (when there is no previous anchor — first run or `--full` — render the previous-anchor field as `none`):

   ```markdown
   # Freshness Sweep Report — YYYY-MM-DD HH:MM:SS UTC

   **Anchor:** upstream/main @ <sha> (previous: <prev-sha-or-"none">)
   **Mode:** diff | full
   **Entries dirty:** N
   **Entries updated:** N
   **Entries flagged:** N
   **Questions accumulated:** N

   ## Updated automatically

   - `<entry id>` — <summary from subagent JSON, or one-line script-driven outcome>
   - ...

   ## Flagged for human review

   ### <file path>
   **Triggers fired:** <list of changed source files>
   **Why:** <reason from flag-on-change marker, subagent flag.reason, or "unmarked editorial">
   **Suggested follow-up:** <subagent flag.suggested_follow_up, else blank>

   ## Questions

   - (`<entry id>`) <question text>

   ## Skipped (errors)

   - (`<entry id>`) <error text>
   ```

2. Write to `docs/freshness/last-report.md` (overwrite).

3. If no `files_changed` from any subagent AND no script touched any files: do not commit, do not push, do not open a PR. Print "Nothing to update — exiting clean." Skip to Phase 8.

4. Otherwise: stage all changed files plus `docs/freshness/last-report.md`.

## Phase 7: Commit, push, open PR

1. Build the commit message body. The title MUST be `docs: freshness sweep — N entries (upstream@<new-anchor-sha>)` — the `(upstream@<sha>)` token is parsed by the next sweep to resolve the prior anchor. Body template:

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
   Previous anchor: <prev-sha-or-"none">
   ```

2. Commit using the project's HEREDOC pattern via the Bash tool. **Critical: the closing `EOF` terminator MUST be at column 0 (no leading whitespace) when actually executed by the Bash tool.** The example below is intentionally rendered at column 0 — preserve that exact alignment when constructing your Bash invocation:

```bash
git commit -m "$(cat <<'EOF'
docs: freshness sweep — 5 entries (upstream@abc1234)

Updated:
- about-page-packages
- dev-stats

Flagged for review (see docs/freshness/last-report.md):
- docs/sections/Teams.md

Mode: diff
Previous anchor: def5678

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

3. Push: `git push -u origin freshness-sweep/$TS`.

4. Open PR. Same column-0 `EOF` requirement as above:

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

1. `cd $REPO_ROOT` (the absolute path captured in Phase 0). This leaves the worktree regardless of nesting depth.
2. Run `git worktree remove $WORKTREE`. If it fails because of uncommitted state (shouldn't happen if Phase 7 succeeded, but possible if Phase 7 errored): use `git worktree remove --force $WORKTREE` (per Peter's memory: never `rm -rf` for worktree cleanup).
3. The branch `freshness-sweep/$TS` stays on origin until the PR is merged or closed (GitHub handles branch lifecycle).

## Failure modes

| Mode | Behavior |
|---|---|
| No `upstream` remote | Error in Phase 1; nothing else runs |
| Catalog YAML parse error | Error in Phase 3; proceed to Phase 8 to tear down worktree |
| Marker validation error in some doc | That doc is added to "Skipped (errors)" in the report; other entries continue |
| Subagent fails (returns malformed JSON or errors) | That entry is skipped; other entries continue; failure recorded in "Skipped (errors)" |
| Two entries write the same target | Schema validation rejects this at parse time |
| Trigger glob matches no real path | Schema validator warns (likely typo or rename — config bug, not runtime error) |
| Push fails (network, auth) | Worktree retained; user fixes credentials and re-runs Phase 7 manually |
| `gh pr create` fails | Worktree retained; report and commit are on the branch; user opens PR manually |

## What this skill does NOT do

- Schedule itself. Cadence is manual (likely daily for diff, weekly for full).
- Merge PRs. CI runs and the merge decision are Peter's.
- Touch files outside the catalog or editorial trees.
- Check the main checkout for dirty state. All work happens in an isolated worktree, so the main checkout's state does not affect this skill.
- Update `docs/architecture/maintenance-log.md` (hand-maintained for non-automated cadences).
