# Phases 7–9: PR, inline round, stand down

## Phase 7: PR

**Self-review the run's own new prose first — only that prose, against the gates below.** Read
`git diff origin/main -- '*.md' '*.yml'` plus every `.cs` comment block the run rewrote: `doctor.py prose-gate --base origin/main`, the trace gate (3c — every symbol, route and path named resolves; every
"only", "never" and "always" is checked), and the render rule — a claim about what a page
shows traces to the `.cshtml` that renders it, not the DTO that feeds it. Text the run wrote
this session is the text most likely to be wrong, and a reviewer here is not free.

**Run `dotnet format whitespace Humans.slnx --verify-no-changes` before pushing, not after CI says
so.** A green build is not the formatting gate — collection-expression line breaks pass the build
and the full test run, and fail code-quality.

```bash
python .claude/skills/section-doctor/doctor.py push   # origin gate + push, one call
gh pr create --repo peterdrier/Humans --base main --title "doctor(<Section>): <headline>" --body ...
```

The title's headline names something a user or reader would notice — the fix, the false
claim corrected, the surface removed — never the run's bookkeeping.

Body: the opening header paragraph (run/section, run-file link, target-shape link) **ends with the
next-up forecast**, read from the `UPCOMING:` line in `$RUNDIR/selection.txt` — "Target shape:
`Docs/health.md` (new). Likely future sections: A, B, C, D." — never buried lower in the body;
omitted when the selector was skipped (`--section`). Then
assessment summary, worked/skipped bullets, and a
**`## Needs Peter`** block — terse, numbered, answerable in a word or two, **citing findings by
number rather than re-describing them** (Phase 5). **The PR body is the authoritative queue while
the PR is open** (resume reads it from there); the run file's copy carries it forward after merge.
One PR per run; never merge.

**Cost report** — before creating the PR, run:

```bash
python .claude/skills/section-doctor/cost-report.py section-doctor/$TS "$RUNDIR/phase-log"
```

It finds this run's own session transcript under `~/.claude/projects` (the model never sees its
own usage in-band, but the harness logs every API call's tokens there), buckets the main thread
by the phase log, adds one row per subagent transcript (named by the `thread:` marker its
prompt opens with), and prints a markdown table with per-row model and API-equivalent $ — plus
footer lines reporting the peak main-thread context and any compactions detected (a compaction
mid-run is exactly when Phase 5's re-read rule earns its keep; if one is reported, say so in the
run file's retro).

**Rows are named by what the run was doing, not by phase number** — each row takes the label from
its `mark` line, and the phase id is a trailing column. Phase 4's per-item marks give one row per
strike, so the largest bucket reads as a breakdown rather than a lump. Whatever the table's rows
are, they are what the reader gets; a run that marks lazily reports lazily.

The table is a **Phase 1 → PR-creation cutoff, not a run total** — the PR
create/backfill calls and any Phase 8 work land after measurement (the footer says so). **Post it
as a PR comment immediately after `gh pr create`** (`gh pr comment <PR#> --body-file` — never
inline shell-quoted; the GitHub MCP comment tool in a cloud run without `gh`). The comment is the
table's only home: one append-only write adjacent to the create call, costing no push, no CI run
and no review round. Never paste it into the PR body or the run file. The table stands on its
own — never compare it against another run's cost or pull in a prior run's figures; cross-run
reading is Peter's, done over the PRs. The script never fails the run — on any discovery problem
it prints `Cost: unmeasured (...)`; post that line as the comment all the same (a failed
measurement leaves a visible record, never silence) and note it in Needs-Peter.

Then backfill the real PR number over every `pending` reference (run file header, health history
row), commit, push again.

**That backfill is the last bookkeeping push.** From here a push must change code, tests, or a
doc a reader depends on. **Never push a commit whose entire content is a corrected figure or a
restated status about the branch** — such a correction rides along with the next substantive
commit, or is skipped. Every push costs a CI run, a preview deploy, a surface report and a review
quota. The same holds for the run file's account of itself: a render checked on the preview
deploy, a compaction the cost comment reports, is written in when a substantive commit next
goes out, or not at all — and never claimed before it happened.

## Phase 8: Inline round (interactive runs only)

If Peter is present, present the Needs-Peter items inline now (terse, numbered, plain prose —
never AskUserQuestion) and apply answers as new commits + push, ticking each answered item in
both the PR body and the run file — Resume mode's grep gate applies here too. Unattended morning
runs skip this; `resume` covers it. Unanswered items carry forward — never re-asked.

## Phase 9: Stand down — the worktree stays

**Stop working. Do not remove the worktree.** A run ends when its PR is merged or closed, not when
it is opened — review arrives after Phase 7 (a BLOCK, bot findings, Peter working the Needs-Peter
queue) and every one of those is answered by committing to this branch.

**A review bot's finding is a sample, not an instance.** Before fixing the reported line, grep the
branch for the class of claim it is an example of — the type, the method, the abolished case.
Fixing only the reported line leaves the siblings and looks resolved.

**Review rounds run under `review-round-budget` and `.claude/skills/steward/SKILL.md`**, and
each push in them is `doctor.py push`, then confirms the PR's head
sha advanced (`pull_request_read`) — a push to the wrong repo is silent: no CI, no review, no
deploy.

**Resolve gate.** A thread is replied to and resolved only after `doctor.py resolve-check <sha>`
passes for the commit the reply cites (it exists and is on `origin/section-doctor/$TS`) and the
line the finding named is re-grepped and reads as the reply claims. A reply names a commit that exists on
`origin` or names none. At the round cap, every finding agreed and left unfixed is listed in the
cap comment and in the run file's `## Needs Peter` — never resolved, never marked fixed.

Phase 9 writes nothing. Phase 7's backfill commit is the run's last write, and everything a later
session needs is already derivable: the branch is `section-doctor/$TS`, its workspace is
`$REPO_ROOT/.worktrees/section-doctor-$TS` locally (`$REPO_ROOT` in a cloud run), and the PR
number is in the run file. `$RUNDIR` is
scratch; leave it for the OS to reclaim. **Leave the worktree clean** — an uncommitted edit here
never reaches the PR and makes the retained worktree dirty for whoever picks the review up.

**Teardown happens when the PR reaches terminal state** — by `/merged`, or by hand with
`git worktree remove $WORKTREE` from `$REPO_ROOT` (never a recursive delete). A cloud run has
nothing to tear down — `$WORKTREE` is the repo root and the container is reclaimed on its own, so
never run `git worktree remove` there.

Phase 2's **`ALL BLOCKED`** exit is the one case that tears down immediately: no PR, no branch
content, nothing to come back for.

