---
name: section-doctor
description: "Daily per-section review cycle driving a section toward the smallest, clearest form that still does everything it does today. Selects its section live each run (scripted: reforge surface score, middle-out, then changed-since-last-run — no stored plan), inventories every file in the section, derives the target shape before running any scan, then works parallel threads — shape, behavior/bugs, freshness, conformance, tests, prose/nav, inbox — into one ranked list and strikes it on a 2-3h budget. One PR per run; each run's report + Needs-Peter queue lives in its own docs/health/runs/ file; 'resume' applies Peter's answers later. Use for the morning section-improvement run, 'doctor <section>', or 'run section doctor'."
argument-hint: "[resume] [--section=<Name>] [--budget=2.5h] [--upstream-issues] [--mutation]"
---

# Section Doctor

Full design: `docs/superpowers/specs/2026-08-17-section-doctor-design.md`.

**Only Peter edits this skill** — this file, `phases/`, `threads/` and the scripts. A run proposes — it records lessons in its run file and its
Needs-Peter block — and never amends its own instructions, in a sweep or otherwise. It is
instructions, not a record: no shas, no dated post-mortems, no accounts of past runs. That
history lives in the run files and the design spec. An issue reference earns its place only by
naming a live contract or a baseline a phase is bound to — never as provenance for a rule that
already stands on its own.

## Intention

Humans is run by volunteers and, increasingly, read and changed by agents. Every section
will be extended, debugged and rewritten many times by people and models who were not there
when it was written, and each of them pays, in attention and in tokens, for every line,
comment, doc claim and test still sitting there. This process exists to keep that price
honest. Once a day one section is read whole, as it stands today, by something with the time
to read all of it; whatever no longer earns its place is removed, whatever the docs claim is
checked against what the code does, and any real defect met on the way is fixed. The measure
is not a score or a diff size: it is that whoever opens the section next finds it smaller,
truer, and doing exactly what it did — and that Peter can trust what the run says it did
without re-reading the section himself.

When a call is ambiguous, ask: *does this make the section truer and lighter for its next
reader without changing what it does for its users, and will Peter be able to see and verify
it?* Yes to both: do it. Anything that changes behaviour, widens surface, retires a guardrail,
or cannot be verified is Peter's call, not the run's.

## Purpose

**Every section converges, run over run, on the smallest and clearest form that still does
everything it does today — and is correct.**

Feature work deposits sediment: duplicated helpers, comments that outlived their decisions, docs
describing a version that shipped two refactors ago, contracts wider than any caller, tests that
assert the mock. Nothing else in this repo removes it — reviews look at diffs, and sediment is in
no single diff. This is the only process that reads a section *as it now stands* rather than as it
was last changed.

A run is judged on three things:

- **Did the section get smaller and clearer without losing anything?** Line count is a fair proxy
  for the token weight every future reader, human or agent, pays to work here. Growth needs a
  stated reason — and cross-section consolidation is a good one, so the figure that matters is the
  **net across every section the run touched**, not a per-section floor. That figure is GitHub's
  diff stats on the run's PR; the run file never restates it (Phase 5).
- **Was every file actually looked at?** A finding-driven pass finds only what sits next to what
  it already suspects. Full coverage is what makes this a review rather than a sweep — and it is
  where the bugs come from.
- **Is the section still doing exactly what it did?** The constraint that buys all the latitude
  above.

**Contract: business functionality does not change.** That constraint is what buys the latitude
to rewrite anything else about the section.

**Concurrency contract (nobodies-collective/Humans#1069): a run writes no file that another
concurrent run also writes.** Runs never merge themselves, so N unattended days mean N open PRs
at once; every one must merge cleanly in any order. The only shared-file writes are each run's
sweep commit (Phase 5), idempotent by construction.

## Invocation

| Form | Behavior |
|---|---|
| *(none)* | daily run: select a section live (Phase 2), doctor it |
| `resume` | no new work — work the Needs-Peter queue (see Resume mode) |
| `--section=<Name>` | skip the selector, doctor this section |
| `--budget=<duration>` | override budget (default 2.5h); wall-clock, checked between items |
| `--upstream-issues` | opt-in upgrade: include `nobodies-collective/Humans` in the Inbox issue review (default: fork only) |
| `--mutation` | opt-in upgrade: section-scoped Stryker in the Tests thread (default: invariant matrix + test quality only) |

The two opt-in flags exist because the standard cloud environment supports neither — no
upstream-repo GitHub scope, no Stryker. **Without its flag, a run never attempts, probes for,
mentions, or records-as-skipped either capability.** The default run is complete without them.

## Files

The skill is a spine plus per-phase files. **Read a phase file when you enter the phase, and
read it again after any compaction that happened inside it** — a compaction summary keeps the
goals and sheds the mechanics, which is exactly how runs have half-done a phase. Nothing below
is optional reading for the phase it governs.

| File | Governs |
|---|---|
| `phases/setup.md` | Phase 0 Setup, Phase 1 Workspace: scratch dir, shell rules, no-compiler runs, phase log |
| `phases/select.md` | Phase 2: open-PR list, selector script, blocked set, first push, session title |
| `phases/assess.md` | Phase 3a–3e: inventory, behavior-first read, target shape and trace gate, threads, ranking and checkpoint |
| `threads/CONTRACT.md` + `threads/<lens>.md` | what a dispatched 3d thread reads and returns; one file per lens |
| `phases/strike.md` | Phase 4: execution order, executors and reviewer, gates, sweeps, skip-and-queue, ledgers |
| `phases/bookkeeping.md` | Phase 5 run file, `health.md` row, prose gate, sweep; Phase 6 retro and amendments |
| `phases/pr.md` | Phase 7 PR and cost comment, Phase 8 inline round, Phase 9 stand down and resolve gate |
| `phases/resume.md` | `resume`: gather the Needs-Peter queue from open PRs and merged run files, apply rulings |
| `select-section.py` | the selection maths (Phase 2) — never re-derived in-band |
| `doctor.py` | the shell mechanics: `rundir`, `mark`, `push` (origin gate), `prose-gate`, `dispatch-log`, `resolve-check` |
| `cost-report.py` | Phase 7's cost table from the phase log and the `thread:` markers |

`doctor.py` derives the run from its branch, so it needs no shell state between tool calls.
Every push of the run goes through `doctor.py push`; a run never calls `git push` directly.

## The run

| Phase | Mark | Outcome |
|---|---|---|
| 0 Setup | — | `$TS`, `$RUNDIR` outside the tree; args parsed; compiler present or a declared docs-only run |
| 1 Workspace | `phase1 worktree` | branch `section-doctor/$TS` off `origin/main`; phase log started |
| 2 Select | `phase2 select section` | one section from the selector, never from judgment; run-file header pushed; `ALL BLOCKED` / `NOTHING CHANGED` stop the run |
| 3 Assess | `phase3 assess` | every file assigned; target written and traced **before** any scan; threads dispatched by lens; one ranked list with an independence verdict; assessment checkpointed to `$RUNDIR/assessment/` |
| 4 Strike | `phase4 strike: <what>` per item | the list drained in `cut → delete → dedup → collapse → rearch` order, each commit gated and swept; Peter's calls queued, never taken |
| 5 Bookkeeping | `phase5 bookkeeping` | run file, `health.md` history row, `## File coverage`, `## Threads`, sweep commit |
| 6 Retro | `phase6 retro` | the retro's questions answered; lessons as numbered Needs-Peter items |
| 7 PR | `phase7 PR` | self-review of the run's prose, format check, push, PR, cost comment, PR-number backfill |
| 8 Inline round | — | interactive runs only: Needs-Peter answered inline and applied |
| 9 Stand down | — | worktree stays until the PR is terminal; review rounds under the steward and the resolve gate |

The 3e→4 boundary is the run's context shed: from there disk (`$RUNDIR/assessment/`, the
phase log, `git log`) is the authoritative state, and every statement about the run's own
conduct is read back from it, never restated from a compaction summary.

## Standing constraints

- Business functionality does not change.
- No EF migrations, schema changes, or data backfills — queue them. No analyzer suppressions.
  Never touch `[DontFix]`.
- Public-surface additions need Peter; dead-surface deletion is the job (reviewer-gated).
  Guardrail retirement needs Peter's go on a brief (`brief-before-retiring-guardrails`); the
  reviewer subagent is never that go.
- Explicit tagged model on every subagent, and a `thread:` marker on every dispatch. Never
  leave the branch red between commits.
- **Every push is `doctor.py push`** — the origin gate in the same call; on a mismatch the run
  stops. A push never goes by remote name to a remote the gate has not just checked.
- **A run touches only:** the section's files (+ callers where a play requires), the section's
  `Docs/health.md` and `Docs/debt.yml`, its own `docs/health/runs/<date>-<Section>.md`, and — in
  the sweep commit only (Phase 5) — the debt ledgers (central, and any
  section's whose debt the sweep is routing), `memory/`; never the run files it sweeps, and
  **never this skill's own files** (Peter edits those; a run proposes in Needs-Peter). Run
  scratch goes to `$RUNDIR`, outside the worktree entirely. Nothing writes
  `docs/architecture/maintenance-log.md`.
- **Every GitHub issue is read-only to every run.** No close, edit, relabel or comment, on any
  issue, ever — 3d's Inbox review recommends and Peter enacts. A run's only GitHub writes are its
  own PR.
- **`docs/architecture/section-conformance.yml` is read-only to every run**, sweeps included.
  Rows are added and removed only at Peter's direction; a run that wants one proposes it in its
  Needs-Peter block.

