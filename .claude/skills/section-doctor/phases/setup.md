# Phases 0–1: Setup and workspace

## Phase 0: Setup

```bash
REPO_ROOT=$(git rev-parse --show-toplevel)
TS=$(date -u +%Y-%m-%dT%H%M%SZ)                                    # the run's identity: branch, run dir, run file
RUNDIR=$(python .claude/skills/section-doctor/doctor.py rundir --ts $TS)   # scratch — OUTSIDE the working tree
```

Parse args; record start time (`date -u`).

**Run scratch never lives in the worktree.** The phase log and the open-PR JSON are working notes,
not deliverables, and a strike that runs `git add -A` commits anything sitting in the tree. The
`.gitignore` entries for `/.phase-log` and `/.prs.json` stay as a backstop.

**No shell variable survives between tool calls.** Once the branch exists (Phase 1), every
`doctor.py` subcommand re-derives the run from `git rev-parse --abbrev-ref HEAD`, and
`RUNDIR=$(python .claude/skills/section-doctor/doctor.py rundir)` re-derives the path in any
call that needs it. `$WORKTREE` is re-set the same way (Phase 1).

**Shell rules that break a run when missed:**

- Write multi-line content — commit messages, run files — with `git commit -F <file>` or a
  file-write tool, or a **quoted** heredoc. An unquoted heredoc executes backticks inside it, and
  PowerShell here-string syntax (`@'…'@`) silently becomes part of the subject line under Git Bash.
- Never run `dotnet build` and `dotnet test` against the same worktree at once — the test host
  holds the output DLLs and the build burns MSB3026 retry rounds on locked files. One at a time.
- Resolve every asserted path from the worktree root. A bare basename test reports a live file
  missing and invites a repo-wide "fix" for a file that was never gone.
- Build/test output stays out of the transcript: every `dotnet build` / `dotnet test` takes
  `-v quiet -clp:ErrorsOnly` per `memory/process/dotnet-verbosity-quiet.md`; if output is still
  long, redirect to a file under `$RUNDIR` and Read it — never pipe through `tail`/`head`/`grep`.
  Every line of build noise in the transcript is re-read by every later turn of the run.

Getting a toolchain is the *environment's* job, not this skill's — a local run and the
scheduled cloud run both start with the SDK, `dotnet-ef` and reforge already there. Never
install one. Mutation scoring (Stryker) runs **only under `--mutation`**: without the flag the
Tests thread is the invariant matrix and test-quality work, complete in itself — no run installs
Stryker, probes for it, mentions it, or records it as skipped or as a degraded analysis. With
the flag, run section-scoped Stryker as one of Phase 3d's background tool threads — from the
worktree, after selection, never here in Phase 0 — with `concurrency: 16` and
`coverage-analysis: off` per `memory/process/stryker-concurrency-coverage.md` (the environment
must already have Stryker — never install). **This paragraph outranks the prompt that invoked the
run.** A scheduled or hand-written prompt saying Stryker is absent, skip the mutation half and
record it skipped-with-reason is stale wording, not a second instruction: without `--mutation`
there is no mutation half to skip and nothing to record. Follow this paragraph, and raise the
prompt's wording as a Needs-Peter item (Phase 6) instead of choosing between the two.

**What is this skill's job is the run you get when there is no compiler** — which is a real
run, not a failed one. If `dotnet build` cannot run at all, this is a **docs-only run**: work
the reading threads, keep strikes to docs, comments and resx, queue every code finding for the
Needs-Peter block rather than editing C# you cannot compile, record each compiler-dependent
thread as skipped-with-reason (3d's rule), and let the PR's CI be the compile gate. A build
that *fails* is not this — that is a normal broken build, diagnosed like any other. Say so in
the run file's header and in the PR body — a run that could not build and does not say so
reads as a run that found nothing to build.

**Every environment caveat is a dated per-session line, never a standing banner** —
`2026-08-24 07:10Z session: no compiler in this container`. The caveat belongs to the session,
not the run: the next session on this branch gets a different environment, and a banner reading
"this run had no compiler" ends up sitting above compiler-confirmed strikes within the hour.

## Phase 1: Workspace

Per [`always-use-worktree`](../../../../memory/process/always-use-worktree.md) — a worktree locally,
the repo root in a cloud run. `$WORKTREE` is the run's workspace either way; the rest of the
skill doesn't care which it is.

```bash
git fetch origin main   # $TS was fixed in Phase 0 — branch, run dir and run file share it
if [ "$CLAUDE_CODE_REMOTE" = "true" ]; then  # ephemeral single-session container — no worktree
  git checkout -b section-doctor/$TS origin/main
  WORKTREE=$REPO_ROOT
else
  git worktree add $REPO_ROOT/.worktrees/section-doctor-$TS -b section-doctor/$TS origin/main
  WORKTREE=$REPO_ROOT/.worktrees/section-doctor-$TS  # EnterWorktree here; all commands run inside
fi
```

Scope is frozen at the branch point — never reconcile against `origin/main` mid-run. Locally, scope
every Glob/Grep to `$WORKTREE`; in a cloud run `$WORKTREE` *is* the repo root and there is nothing
to scope away.

**Every push of the run, Phase 2 through Phase 9, is `doctor.py push`** — it runs the origin
gate (`origin` must be `peterdrier/Humans`) and the push in one call, because a cloud container
can rewrite the remotes on any resume and a check at setup proves nothing about a push hours
later. On a mismatch nothing is pushed and the run stops; never repoint the remote and retry,
and never `git push` by hand.

**Scope history checks to a named branch or ref, never `git log --all`** — on a run with a blocked
branch set, `--all` surfaces commit subjects from that set and is not blindfold-safe.

**Start the phase log now.** Phase 7's cost report buckets the session transcript by these
marks and names each row by the **label**, not the phase id — a table of phase numbers tells
its reader nothing about where the run's money went:

```bash
python .claude/skills/section-doctor/doctor.py mark phase1 worktree
```

One mark per shell call, appended *before* the work it names starts — two marks in one call
share a timestamp and fold together; a mark written after the work prices it into the row
before:

| Phase | Mark |
|---|---|
| 2 | `mark phase2 select section` |
| 3 | `mark phase3 assess` |
| 4 | `mark phase4 strike: <what>` — **once per strike item**, which turns the run's biggest row into a per-item breakdown |
| 5 | `mark phase5 bookkeeping` |
| 6 | `mark phase6 retro` |
| 7 | `mark phase7 PR` |

A phase that does not mark is a phase nobody can price — its spend silently joins the row above
it. If a mark was missed, append it late rather than not at all and say so in `## Threads`; an
out-of-order log is still bucketed correctly (the report sorts by timestamp), a missing one is not.
