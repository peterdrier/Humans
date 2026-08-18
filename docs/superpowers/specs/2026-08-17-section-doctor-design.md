# Section Doctor — Design

Spec for `/section-doctor`, the daily per-section review cycle. Designed in dialogue with Peter
2026-08-17; decisions below are his calls. Amended 2026-08-18 for conflict-free concurrent runs
(nobodies-collective/Humans#1069, decision 11).

## Problem

The per-section improvement skills (section-align, audit-surface, trim-tests, simplify,
section-read-split, reuse-review, nav-audit, the refactor-swarm lane process) each cover one axis
and are dying of obscurity — half of them are never remembered. Meanwhile sections accumulate AI
slop (wasteful comments, 500-word docs that should be 50, test bloat), surface creep, and doc
drift between deliberate refactor waves. With #866 (G5) nearly done, every section is a project
under `src/Sections/` and can be reviewed as a unit.

## Decisions (from design dialogue)

1. **Evaluate-then-strike, with a plan cache.** A planner orders **every** section into one
   full-cycle queue (~42 rows ≈ 2–3 weeks at 2 runs/day); runs execute from it without
   replanning unless the plan is exhausted or stale. (Not a fixed per-run checklist.)
2. **One macro skill.** `/section-doctor` is the single entry point; the existing per-section
   skills become plays in its toolbox, named by plan items. They remain directly invocable.
3. **Self-amending.** Every run ends with a retro; mechanical lessons queue in the run file's
   sweep queue and the next replan applies them to the skill's own files (see decision 11);
   rubric-level changes queue for Peter; durable rules graduate to `memory/` atoms the same way.
4. **Toolbox in:** section-align, audit-surface, section-read-split, trim-tests, simplify,
   reuse-review (run against the section's own surface, not a diff), the refactor-swarm per-lane
   process (`.codex/skills/humans-refactor`), debt-sweep (absorbed — its ledger becomes a planner
   input), nav-audit (as a per-section slice), resharper (absorbed as a per-section InspectCode
   lane — 2026-08-16 retro; the weekly repo-wide `/resharper` retires once rotation covers every
   section), test-site/run (runtime verification of UI strikes). **Out:** freshness-sweep,
   nuget-*, triage-as-process (its issue outputs are planner and run-day inputs).
   **refactor-swarm** becomes the wrapper that runs multiple section-doctor lanes in parallel,
   especially for coordinated cross-section changes.
5. **All surfaces in scope:** code, tests, docs, comments, GUI/nav, translations. Standing theme:
   AI-slop removal. Hard constraint granting the latitude: **business functionality does not
   change.**
6. **Artifacts:** per-section scorecard at `src/Sections/Humans.<X>/Docs/health.md` (one-page
   current state, last 3 assessments kept ≈ one quarter of history at one app-wide run/day);
   global plan at `docs/health/plan.md`; one report file per run under `docs/health/runs/`.
   Sections not yet in `src/Sections/` are ignored — they'll be gone soon.
7. **Morning job, mostly autonomous.** Judgment items never block a run; they queue in a
   "Needs Peter" block (PR body + the run's file). `/section-doctor resume` applies Peter's
   answers later. Unanswered items carry forward, never re-asked.
8. **Planner uses mid-level signals** — that's why it only runs once per cycle. Staleness
   dominates (stalest first, so a just-merged section lands at the end of the queue); tiebreak
   by score-growth-since-last-review (a section +10% since its review outranks one +3%), then
   issue/debt/churn color.
9. **Run day inhales the whole section** and imagines the from-scratch ideal — what `/simplify`
   would do with a magic wand. That not being possible, it records the ideal shape and spends the
   budget on the biggest-value moves toward it. Budget 2–3h to start, with a few background
   subagent threads.
10. **Old skills are not deleted yet.** Section-doctor must prove itself first; a cutover
    checklist (below) retires them when Peter says go.
11. **Conflict-free concurrent runs (#1069, 2026-08-18).** Runs never merge themselves, so N
    unattended days (at 1–2 runs/day) mean N open PRs that must merge cleanly in any order. So:
    **a run writes no file another concurrent run also writes** — its own
    `docs/health/runs/<date>-<Section>.md`, its section's `Docs/health.md` (the blocked set
    guarantees one open run per section), and the section's own files. `log.md` and
    `last-report.md` are deleted — the runs directory is the log, the newest file is the last
    report, and no generated index replaces them. `plan.md` carries no tick/status column;
    done-ness is derived (health.md date vs. plan anchor) so selection can be recomputed fresh
    every run. Shared files (`plan.md`, the skill's own files, `debt-ledger.yml`, `memory/`) are
    written only by replanning runs — rare (~once per cycle) and effectively serialized by the
    schedule, with **no locking** (Peter's call): a rare overlap costs one hand-resolved
    conflict. Other runs queue such writes in their run file's `## Sweep queue` for a later
    replan to apply — sweeps are windowed by anchor commit and never edit the swept run files,
    leaving `resume` their only post-merge editor. Daily runs no longer write
    `docs/architecture/maintenance-log.md` at all.

## Invocation

| Form | Behavior |
|---|---|
| *(none)* | the daily run: replan if needed, then execute today's plan entry |
| `resume` | no new work: read the Needs-Peter queue, take Peter's answers, apply them on the queued items' branches |
| `--section=<Name>` | skip the plan, doctor this section today |
| `--budget=<duration>` | override budget (default 2.5h). Wall-clock, checked between items |
| `--replan` | force a fresh plan even if the current one is neither stale nor exhausted |

## Artifacts

### `src/Sections/Humans.<X>/Docs/health.md` (per section, machine-maintained)

One page. Freshness-sweep ignores it (machine-owned).

```markdown
# <Section> — Health

Last assessed: <date> @ <commit>

## Scorecard
| Axis | State |
|---|---|
| Reforge (surface / internal) | N / N |
| Tests | count, mutation score if run, one-line verdict |
| Docs vs code | one-line verdict |
| Comments / slop | one-line verdict |
| GUI / nav | one-line verdict |
| Translations | missing-string count |
| Arch conformance | smells, or "clean" |

## Ideal shape
<the magic-wand vision — a few sentences on what this section would be if rewritten from
scratch today, and the gap between that and what exists>

## Opportunities (ranked by value)
1. <item — play — est. size>   ← unworked items carry to the next assessment

## History (last 3 assessments)
| Date | Reforge | Tests | Outcome | PR |
```

### `docs/health/plan.md` (global, written only by replans)

```markdown
# Section Doctor — Plan

Anchor: <commit> (<UTC date>)

| # | Section | Focus (from planner signals) |
```

No status column and no Needs-Peter block — done-ness is derived (a row is done when its
section's `health.md` last-assessed date is on or after the anchor date; blocked while an open
section-doctor PR names it), and Needs-Peter items live in run files.

### `docs/health/runs/<yyyy-mm-dd>-<Section>.md` (one per run)

The run's report — the only global file a non-replanning run writes; no two runs ever touch the
same path (`-<HHMMZ>` suffix on the rare same-day collision). The directory is the log and the
newest file is the last report; there is no `log.md`, `last-report.md`, or generated index.
Old files get purged manually after a while. Sections:

```markdown
# <date> — <Section>

Run: <invocation>, anchor <commit>, budget <n>h. PR: peterdrier/Humans#N

## Assessment summary
## Worked
## Skipped / queued        ← includes plan rows passed over as blocked (`<section> — open PR #N`)
## Retro
## Needs Peter
- [ ] <one-line question>  ← authoritative in the PR body while open; here after merge
## Sweep queue             ← shared-file writes; a later replan's window applies them (no ticks)
- lesson: <dated one-liner for the skill's Lessons>
- debt: <debt-ledger inbox entry>
- memory: <bucket>/<name> — <rule>
```

## The planner

Runs only when there is no plan, no plan row is selectable, `--replan` is passed, or the plan is
stale. Staleness is a judgment call with a guideline: a merged change since the anchor that
materially reshapes an upcoming scheduled section (move, rename, major feature) justifies
replanning; routine churn does not. Threshold expected to be tuned by the learning loop.

**Replans are the only writers of shared files** (`plan.md`, the skill's files,
`debt-ledger.yml`, `memory/`), and they are rare — a full-cycle plan outlasts ~2 weeks of
2×/day runs, invocations are scheduled one at a time, and later runs read the open replan's
plan from its branch (newest anchor wins) instead of writing their own. There is **no lock**
(Peter's call, PR #1366); a rare overlap costs one hand-resolved conflict, not corruption.
Besides writing the plan, the replanning run **sweeps merged run files by anchor window** — files
that landed under `docs/health/runs/` between the previous plan's anchor commit and the new
anchor (`git diff --name-only <prev-anchor>..origin/main -- docs/health/runs/`; all of history
when no previous plan exists): every `## Sweep queue` item is applied to its shared target,
skipping items already present (idempotence covers a run that merges late and straddles
windows). The swept run files themselves are **never edited** — `resume` is their only
post-merge editor, so resume's edits cannot collide with a concurrent sweep.

Signals (mid-level — no deep reading):

- `reforge surface-score --format compact` (size + deltas)
- Last-assessed date from each `health.md` — the primary order: never-assessed first (score
  descending; seed last-served knowledge from merged run files under `docs/health/runs/`), then
  stalest first, so a just-merged section lands at the end of the queue
- Tiebreak: **score growth since last assessment** (percentage), then open GitHub/in-app issue
  counts per section, `debt-ledger.yml` items touching the section, churn under the section's
  paths since its last assessment
- **Include blocked sections** — their PRs merge mid-cycle and their recent assessment dates put
  them at the queue's end anyway; exclude only sections with in-flight or imminently-planned
  feature work (check the active sprint plan)

Output: the full-cycle table (every `src/Sections/` project) in `docs/health/plan.md`.
The plan is advisory — run-day findings can extend a section's stay.

## Run phases

- **0 Setup** — parse args, record start time, `REPO_ROOT`.
- **1 Worktree** — `git fetch origin main`; worktree at `.worktrees/section-doctor-<TS>`, branch
  `section-doctor/<TS>` off `origin/main`. Scope frozen at branch point. All Glob/Grep scoped to
  the worktree.
- **2 Plan check** — find the live plan first: only replans write `plan.md` and the replan run's
  PR may still be open — discover open `section-doctor/*` branches by `headRefName` prefix
  (GitHub's `head:` search qualifier matches exact names, not prefixes), read `plan.md` from
  `origin/main` and each open tip, newest anchor wins; never commit another run's plan or run
  files into this branch. The open PRs' titles are the **blocked set**. Selection is derived,
  recomputed fresh each run: take the first plan row that is neither done (its `health.md`
  last-assessed date on `origin/main` ≥ anchor date) nor blocked. No selectable row → replan
  (building first — an unbuilt solution under-reports reforge scores); every section blocked →
  the one genuine no-work exit.
- **3 Deep assessment** (the expensive judgment, once per section per cycle) — inhale the section
  front to back, baseline build first (reforge needs it). Parallel subagent lanes where useful
  (models explicit + tagged): code/arch lane (audit-surface posture with per-method caller
  counts, smells, reforge, reuse-review checklist, flow-trace simplification pass), tests lane
  (good/bad/ugly triage; **section-scoped Stryker kicked off in background**; the **invariant
  coverage matrix** — every invariant/negative rule/trigger in the section doc mapped to a
  pinning test), InspectCode lane (`jb inspectcode` scoped to the section), docs lane (section
  `Docs/*.md` + `docs/guide/<Section>.md` vs code; trigger-glob verification), surface lane
  (slop: comments, verbosity, dead strings, translations, nav dead-ends), inbox (section-tagged
  debt-ledger items, open GitHub issues, in-app issues). Write the refreshed scorecard +
  **ideal shape** + ranked opportunities into `health.md`.
- **4 Strike** — work the ranked list top-down within budget, and **drain it** — stopping early
  with strikeable items remaining is a failure mode (2026-08-16 retro: the shakedown stopped at
  40 of 150 minutes). Each item names its play: a toolbox skill run scoped to the section, or a
  direct fix. One item / tight cluster per commit; build + targeted tests per item; full
  `dotnet test Humans.slnx -v quiet` before each push; push every 3–5 items. Budget checks are
  real `date` reads, never estimates. Doc fixes sweep the claim across `docs/guide/` and sibling
  docs; UI-affecting strikes get runtime verification in the running app. Non-mechanical changes
  (deletions beyond dead code, structural moves) get a second-opinion reviewer subagent
  (opus-tier, score-blind, default-reject — refactor-swarm posture).
- **5 Bookkeeping** — exactly two writes, both conflict-free: the `health.md` history row and
  this run's own `docs/health/runs/<date>-<Section>.md` (all in the worktree, same PR). No
  shared file is touched; daily runs never write `maintenance-log.md`.
- **6 Retro + self-amend** — what was planned vs. what helped, wasted motion, rubric misses —
  written into the run file. Mechanical lessons → the run file's `## Sweep queue` as `lesson:`
  items (the next replan applies them to the skill's files; never edited directly mid-run —
  they are shared). Judgment lessons → Needs-Peter queue. Durable rules → sweep queue as
  `memory:` items. All Phase 5–6 edits are committed before the Phase 7 push — nothing lands
  after it.
- **7 PR** — one PR per run to `peterdrier/Humans:main`. Body: assessment summary, worked/skipped,
  **Needs Peter** block — authoritative while the PR is open; the run file's copy carries it
  forward after merge. After creation, the real PR number is backfilled over the `pending`
  placeholders (run file header, health history row) in one more commit + push. Never merges.
- **8 Inline round (interactive runs only)** — if Peter is present, present the Needs-Peter items
  inline (debt-sweep Phase 7 doctrine: terse, numbered, no AskUserQuestion) and apply answers
  now. Unattended runs skip this; `resume` covers it.
- **9 Teardown** — `git worktree remove` (never `rm -rf`).

## Resume mode

`/section-doctor resume`: gather the queue from both places an item can live — `## Needs Peter`
blocks in open `section-doctor/*` PR bodies (discovered by `headRefName` prefix; authoritative
for unmerged runs, whose run files exist only on the PR branch) and unticked `## Needs Peter`
entries in `docs/health/runs/*.md` on `origin/main` (merged runs). Present the items inline,
then apply: open-PR items as commits on that PR branch, ticking **both** the PR body and the
branch's run file (an unticked run file would resurface after merge and be re-applied); merged
items **grouped by run file** — one fresh worktree and one PR per run file, carrying all of
that file's answers (an answer pushed with no PR is stranded; several same-file answers in
separate PRs would conflict with each other) plus teardown — one writer per run file, so these
PRs cannot conflict with each other or with concurrent doctor runs. No new assessment work.

## Standing constraints

- **Business functionality does not change.** That is the contract that buys the latitude.
- No surgical fixes (constitution). No EF migrations, schema changes, or data backfills — schema
  opportunities go to the Needs-Peter queue. No analyzer suppressions; never touch `[DontFix]`.
- Public/interface surface *additions* need Peter (reuse-first discipline) → queue. Deletions of
  dead surface are in scope (that's the job), gated by the reviewer subagent.
- Explicit model on every subagent, tagged in name + description.
- Section projects only (`src/Sections/`); pre-G5 remnants are out of scope.
- A run touches only: the section's files, its callers where a play requires (e.g. read-split
  migrations), the section's `health.md`, and its own `docs/health/runs/` file. A replanning run
  additionally touches: `plan.md`, the skill's own files, `debt-ledger.yml`, and `memory/` —
  never the run files it sweeps. Nothing writes `maintenance-log.md`.

## Relationship to existing skills — cutover checklist

Until section-doctor has proven itself over several runs (Peter's call), nothing is deleted.
At cutover:

- [ ] Retire `/debt-sweep` (skill + maintenance-log row marked absorbed; `debt-ledger.yml`
      survives as a planner input and strike source)
- [ ] Retire standalone `/nav-audit` (absorbed as the per-section nav slice)
- [ ] Retire the weekly repo-wide `/resharper` process (absorbed as the per-section InspectCode
      lane) once rotation has covered every section
- [ ] Demote `section-align`, `audit-surface`, `section-read-split`, `trim-tests`, `simplify`,
      `reuse-review` descriptions to note they are section-doctor plays (kept invocable)
- [ ] Repoint `refactor-swarm` at section-doctor lanes
- [ ] Retire the Section Refactor History table in `maintenance-log.md` (selection duty and data
      live in the planner + `health.md` files)

`freshness-sweep` is unaffected (repo-wide doc drift, diff-triggered); section-doctor's docs lane
is the deep per-section complement.

## Failure modes

| Failure | Behavior |
|---|---|
| Plan/health file parse error | Abort before work; report |
| Reforge unavailable | Assess without scores; note in report |
| Item breaks build/tests, can't be made right | Revert item, record, continue |
| Reviewer subagent rejects twice | Revert, skip, record |
| Budget hit mid-list | Normal: commit what's done; `health.md` carries the remainder |
| Push / PR fails | Worktree retained; fix manually |
| Section has in-flight feature work discovered mid-run | Stop the strike phase, ship the assessment-only PR, note in the run file |
