---
name: section-doctor
description: "Daily per-section review cycle. Reads the global plan at docs/health/plan.md (replanning from mid-level signals when exhausted or stale), inhales today's section front to back, refreshes its Docs/health.md scorecard + ideal shape, then spends a 2-3h budget on the biggest-value behavior-preserving improvements across code, tests, docs, comments, nav, and translations. One PR per run; judgment items queue in a Needs-Peter block; 'resume' applies Peter's answers later. Use for the morning section-improvement run, 'doctor <section>', or 'run section doctor'."
argument-hint: "[resume] [--section=<Name>] [--budget=2.5h] [--replan]"
---

# Section Doctor

Full design: `docs/superpowers/specs/2026-08-17-section-doctor-design.md`. This file is
self-amending — Phase 9 edits it from run lessons; keep those edits terse and dated.

**Contract: business functionality does not change.** That constraint is what buys the latitude
to rewrite anything else about the section.

## Invocation

| Form | Behavior |
|---|---|
| *(none)* | daily run: replan if needed, execute today's plan entry |
| `resume` | no new work — work the Needs-Peter queue (see Resume mode) |
| `--section=<Name>` | skip the plan, doctor this section |
| `--budget=<duration>` | override budget (default 2.5h); wall-clock, checked between items |
| `--replan` | force a fresh plan |

## Phase 0: Setup

`REPO_ROOT=$(git rev-parse --show-toplevel)`. Parse args; record start time (`date -u`).

## Phase 1: Worktree

```bash
git fetch origin main
TS=$(date -u +%Y-%m-%dT%H%M%SZ)
git worktree add $REPO_ROOT/.worktrees/section-doctor-$TS -b section-doctor/$TS origin/main
WORKTREE=$REPO_ROOT/.worktrees/section-doctor-$TS  # cd here; all commands run inside
```

Scope is frozen at the branch point — never reconcile against `origin/main` mid-run. Scope every
Glob/Grep to `$WORKTREE`.

## Phase 2: Plan check

**Find the live plan state first.** The previous run's PR may still be open (runs never merge),
so the newest plan/log/queue may exist only on that PR branch, not on `origin/main`. Discover:

```bash
gh pr list --repo peterdrier/Humans --state open --json number,headRefName \
  --jq '.[] | select(.headRefName | startswith("section-doctor/"))'
```

(`--search "head:..."` matches exact branch names, not prefixes — don't use it.) If an open run
branch exists, fetch it and read `docs/health/*` from its tip; else read `origin/main`'s copy.
Carry that state forward in this run's commits so plan/log history never forks.

Replan when: no plan, plan exhausted, `--replan`, or a merged change since the plan's anchor
materially reshapes an upcoming scheduled section (move/rename/major feature — routine churn is
not staleness).

**Replanning** (mid-level signals only — no deep reading):

1. `dotnet build Humans.slnx -v quiet` first — an unbuilt solution silently under-reports
   Reforge scores — then `reforge surface-score --format compact` for size + deltas. (The build
   also serves Phase 3/4.)
2. Last-assessed dates from every `src/Sections/Humans.*/Docs/health.md`. First cycle:
   never-assessed first, score descending (seed last-served from the Section Refactor History
   table in `docs/architecture/maintenance-log.md`). After the first full cycle: rank primarily
   by **score growth since last assessment** (+10% outranks +3%), then staleness.
3. Tiebreak color: open issues per section, `docs/architecture/debt-ledger.yml` items,
   churn under the section's paths since its last assessment.
4. Skip sections with in-flight or imminently-planned feature work (check the active sprint plan).

Write the 5–7 day table + anchor to `docs/health/plan.md`. Consecutive days for one section are
allowed. The plan is advisory — today's findings may extend a section's stay.

Take today's section (or `--section`). Sections are `src/Sections/` projects only.

## Phase 3: Deep assessment

Start `dotnet build Humans.slnx -v quiet` (background) immediately — reforge scores need a
built solution, and the build doubles as the strike phase's baseline.

Inhale the section front to back — this is the once-per-cycle expensive judgment. Dispatch
parallel background lanes where useful; **every subagent gets an explicit model, tagged in name
and description** (sonnet for mechanical scanning, opus-tier only where judgment earns it):

- **Code/arch lane** — audit-surface posture on the section's services/interfaces with
  per-method external-caller counts (reforge makes this cheap); smells against
  `peters-hard-rules.md` + `design-rules.md`; reforge surface + internal score; dead surface;
  reuse-review's unnecessary-surface checklist against the section's own Contracts;
  a flow-trace simplification pass — walk each service/repository flow asking "is there a
  simpler shape" (overlapping methods, pass-throughs, duplicated pipelines).
- **Tests lane** — good/bad/ugly triage of the section's tests (slop, redundancy); **kick off
  section-scoped Stryker in the background at lane start** — score goes in the scorecard,
  surviving mutants seed test strikes; build the **invariant coverage matrix**: every
  invariant, negative access rule, and trigger in the section doc mapped to a pinning test —
  each gap is a ranked opportunity.
- **InspectCode lane** — `jb inspectcode` scoped to the section's project(s) (see `/resharper`
  for invocation); Tier 1/2 findings become strike items.
- **Docs lane** — the section's `Docs/*.md` and `docs/guide/<Section>.md` vs code: do the
  business docs match what the code actually does; verify the section doc's
  `freshness:triggers` globs still resolve.
- **Surface lane** — AI slop: wasteful comments, 500-words-that-should-be-50 docs/messages,
  dead resources, missing translations, per-section nav quality (dead ends, missing backlinks).
- **Inbox** — section-tagged items in `docs/architecture/debt-ledger.yml`, open GitHub issues,
  and in-app issues: work or rank them; off-section finds go to the debt inbox.

Then, from the whole picture, write the **ideal shape**: what this section would be if rewritten
from scratch today (`/simplify` with a magic wand), and rank the concrete moves toward it by
value. Refresh `src/Sections/Humans.<X>/Docs/health.md` (format in the spec; keep last 3
history rows, prune older).

## Phase 4: Strike

Work the ranked list top-down until budget exhausted. **Drain the list — stopping early with
strikeable items remaining is a failure mode, not a judgment call.** Budget checks are real
`date` reads between items, never estimates. Per item (one item or tight cluster per commit):

1. Pick the play: a toolbox skill scoped to the section — `section-align`, `trim-tests`,
   `simplify`, `section-read-split`, `reuse-review` (against the section's own surface),
   the `.codex/skills/humans-refactor` lane process, a `debt-ledger.yml` item — or a direct fix.
2. Fix it right — no surgical fixes. Reuse-first.
3. `dotnet build Humans.slnx -v quiet`; targeted tests for the touched area.
4. Non-mechanical changes (deletions beyond plainly-dead code, structural moves) → second-opinion
   reviewer subagent, opus-tier, score-blind, default-reject: "name the concept that improved in
   one sentence." Reject → rework once; second reject → revert, record.
5. **Doc fixes sweep the claim**: a wrong statement fixed in one doc must be grepped across
   `docs/guide/`, other section docs, and the access matrix — it rarely lives in one file.
6. **UI-affecting strikes get runtime verification**: render the changed page in the running app
   (`dotnet run` + browser/test-site) before the PR — a green build does not prove a cshtml/JS
   change works.
7. Commit `doctor(<section>): <what>`. Full `dotnet test Humans.slnx -v quiet` before each push;
   push every 3–5 items.

**Skip-and-queue classes** (never block the loop): schema/EF changes of any kind, public/interface
surface *additions*, privilege changes, anything needing Peter's judgment → skip, queue for
Phase 7's Needs-Peter block. Off-section debt discovered → `debt-ledger.yml` inbox, never chased.
If in-flight feature work on this section surfaces mid-run → stop striking, ship the
assessment-only PR, note it in the plan.

## Phase 5: Bookkeeping

In the same worktree/PR: `health.md` history row; tick today's plan row (if a plan exists —
`--section` runs have none); append
`docs/health/log.md` (`| date | section | what ran | outcome | PR |`); overwrite
`docs/health/last-report.md` (assessment summary, worked, skipped + why); update this run's row
in `docs/architecture/maintenance-log.md` per `maintenance-log-update`. PR-reference cells are
written as `pending` here — Phase 7 backfills them once the PR number exists.

## Phase 6: Retro + self-amend

Three questions, answered honestly in `last-report.md`: what did the plan/rubric get wrong, what
was wasted motion, what did the assessment miss that striking revealed. Then:

- **Mechanical lessons** → edit this skill's files now (dated one-liners in Lessons below).
- **Judgment lessons** (rubric axes, thresholds, play choices) → the Needs-Peter block.
- **Durable project rules** → `memory/<bucket>/<name>.md` atom + INDEX line, same commit.

Commit all Phase 5 + 6 edits before Phase 7 pushes — the only thing that lands after is
Phase 7's own PR-number backfill commit.

## Phase 7: PR

```bash
git push -u origin section-doctor/$TS
gh pr create --repo peterdrier/Humans --base main --title "doctor(<Section>): <headline>" --body ...
```

Body: assessment summary, worked/skipped bullets, and a **`## Needs Peter`** block — terse,
numbered, answerable in a word or two. **The PR body is the authoritative queue while the PR is
open** (resume reads it from there); mirror the block into `docs/health/plan.md` (committed
before the push) so merged runs carry it forward. One PR per run; never merge.

Then backfill the real PR number over every `pending` reference (log row, health history row,
plan mirror), commit, push again.

## Phase 8: Inline round (interactive runs only)

If Peter is present, present the Needs-Peter items inline now (terse, numbered, plain prose —
never AskUserQuestion) and apply answers as new commits + push. Unattended morning runs skip
this; `resume` covers it. Unanswered items carry forward — never re-asked.

## Phase 9: Teardown

`cd $REPO_ROOT && git worktree remove $WORKTREE` (never `rm -rf`).

## Resume mode

`resume` gathers the queue from both places an item can live, then works it. No new assessment
or strike work.

1. **Open runs:** discover by branch-name prefix — `--search "head:..."` matches exact names,
   not prefixes:
   ```bash
   gh pr list --repo peterdrier/Humans --state open --json number,headRefName \
     --jq '.[] | select(.headRefName | startswith("section-doctor/"))'
   ```
   Each PR body's `## Needs Peter` block (authoritative for unmerged runs; their plan.md
   entries only exist on the PR branch).
2. **Merged runs:** unticked `## Needs Peter` entries in `docs/health/plan.md` on `origin/main`.

Present the open items inline, then apply each answer:

- **Open-PR item** — commits on that item's PR branch (reuse its worktree, or recreate from the
  branch). Tick the item in **both** places: the PR body *and* the branch's `docs/health/plan.md`
  mirror — an unticked mirror would resurface as a merged-queue item after the PR lands and get
  re-asked or applied twice. Push.
- **Merged item** — fresh worktree + branch off `origin/main`, apply the answer, tick the plan.md
  entry, push, **open its own PR** (an answer pushed to a branch with no PR is stranded), tear
  the worktree down.

## Standing constraints

- Business functionality does not change.
- No EF migrations, schema changes, or data backfills — queue them. No analyzer suppressions.
  Never touch `[DontFix]`.
- Public-surface additions need Peter; dead-surface deletion is the job (reviewer-gated).
- Explicit tagged model on every subagent. Never leave the branch red between commits.
- Touches only: the section's files (+ callers where a play requires), `docs/health/*`, the
  section's `health.md`, `docs/architecture/maintenance-log.md` (Phase 5 row),
  `docs/architecture/debt-ledger.yml` (off-section debt inbox), this skill's files, `memory/`.

## Lessons

(Phase 8 appends dated one-liners here.)

- 2026-08-16: resx/XML edits must be structure-aware (python/XML tooling), never line-based sed
  — neutral resx was one-line-per-entry but all 5 language variants were multi-line; sed
  corrupted them and only the build caught it.
- 2026-08-16: keep a by-hand read of the section's auth paths in the assessment — the doc-code
  contradiction on phase gating was invisible to grep and to every lane.
- 2026-08-16 (retro round 2, Peter): the shakedown run stopped at 40 of 150 minutes with
  strikeable items still ranked — hence the drain-the-list rule; and absorbed abilities were
  going unused (Stryker, InspectCode, invariant matrix, claim sweep, runtime verify, inbox) —
  hence the expanded lanes above.
