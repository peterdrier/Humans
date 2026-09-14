# Phase 2: Select the section

Selection is computed live every run — nothing is stored. There is no `docs/health/plan.md` and
no replan machinery; never reintroduce either. A checked-in plan goes stale the moment merges
pause, and these runs must keep going unattended.

**Fetch the open-PR list once** (main thread, cheap):

```bash
gh pr list --repo peterdrier/Humans --state open --limit 200 \
  --json number,headRefName,title,files > "$RUNDIR/prs.json"   # scratch, outside the tree
```

(`--limit` is mandatory — `gh pr list` fetches only 30 by default, so an older open run
silently drops out without it. `--search "head:..."` matches exact branch names, not prefixes
— don't use it.) In a cloud session without `gh`, write the same JSON shape from the GitHub
MCP tools: `[{number, headRefName, title, files: [paths]}]` for all open PRs.

**Then run the selector script** — the selection maths is scripted, not a subagent; a subagent
remains only for the re-doctor judgment below:

```bash
python .claude/skills/section-doctor/select-section.py --prs "$RUNDIR/prs.json" \
  | tee "$RUNDIR/selection.txt"
exit "${PIPESTATUS[0]}"   # tee would otherwise mask the selector's exit code (2/3 below)
```

It computes the **blocked set** — sections named by open `section-doctor/` PRs' titles
(`doctor(<Section>): …`) **and by `origin`'s recent `section-doctor/*` branches that have no PR
yet** (read from the branch's run-file path; a run is still assessing for hours before its PR
exists) — the pool (every `src/Sections/` project), the **feature-active down-rank** (sections
touched by open non-doctor PRs sink to the tier bottom — picked only when nothing else is
eligible there, never excluded), the tiers (previously-doctored iff
`src/Sections/Humans.<X>/Docs/health.md` exists at the branch point; never-doctored always
outranks), builds the solution (an unbuilt solution silently under-reports Reforge scores; the
build also serves Phase 3/4), runs `reforge surface-score --format compact` (a section reforge
has nothing to report on scores 0, never "unknown, ranked last"), and picks:

- **never-doctored tier:** the **median** by score — middle-out: the process proves itself on
  mid-sized sections; the biggest and smallest get their turn once the middle has been worked.
- **re-doctor tier** (only once the never-doctored tier is empty): a section is eligible only
  if its files — guide page included — changed on `origin/main` since the commit that added
  its newest run file (a doctor run is exactly that; any other edit to `health.md` is not one);
  ranked oldest last run first, ties by lowest score. The pick carries a `BASE: <sha>` line —
  Phase 3 diffs against it instead of re-reading the section cold.

It prints `SECTION:` / `TIER:` / `RATIONALE:` plus the full ranked table for the run file, and
an **`UPCOMING:`** line — the next 4 sections a repeat of this maths would pick, assuming each
pick blocks itself and nothing else changes. The `tee` to `$RUNDIR/selection.txt` is what lets
Phase 7 read that line hours later, after the selector's output has left context. The forecast
is purely informational (PR body header, Phase 7): no later run reads or honours it. The script
falls back to a LOC ranking (flagged in its output) when reforge is unusable. Act on its
verdicts — never re-derive the maths in-band, and never pick by judgment:

- **`ALL BLOCKED`** (exit 3) and **`NOTHING CHANGED`** (exit 2, every eligible section
  doctored and untouched since): report and stop. These are the paths that remove the worktree
  immediately (Phase 9) — nothing has been written yet, so it is clean and
  `git worktree remove` succeeds without `--force`. In a cloud run there is no worktree to
  remove: just stop.

Sections passed over as blocked are noted in this run's run file under skipped
(`<section> — open PR #N` or `— branch <name>`); they need no other bookkeeping.

Take the selected section (or `--section`, which skips the selector but never the blocked
set — check it with `select-section.py --prs "$RUNDIR/prs.json" --blocked-only`). Sections
are `src/Sections/` projects only.

**Make the run visible now.** Commit the run file's header alone
(`docs/health/runs/<yyyy-mm-dd>-<Section>.md`, Phase 5's naming; invocation, anchor commit,
branch, budget, `PR: pending`) and push the branch with `doctor.py push`. That file's
path is what the next selector reads the section from; a branch with no push is invisible to
it. No CI runs on a branch push without a PR.

**Name the run after the section, here, once.** Every scheduled run is titled after the routine;
rename the session now — `set_session_title` on the claude-code-remote MCP server, with this
session's own id from `get_session` (no `session_id` describes the caller):

    section-doctor: <Section> — <yyyy-mm-dd>

Skip it without comment when either tool is unavailable; never rename again later.

**A low reforge score is not evidence the section is healthy.** The score measures structure, never
correctness — the lowest-scoring section in the solution was failing open on access control. Nothing
in the ranking rubric surfaces that, so never read a good score as a reason to look less hard.

**Never work a section in the blocked set.** A section with an open section-doctor PR or
branch has unmerged strikes that today's run cannot see — re-doctoring it duplicates work and
produces conflicting PRs. A `--section` naming a blocked section stops like the all-blocked
case — merge the open PR first, or use `resume` to work its Needs-Peter queue.

