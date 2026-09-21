# Daily Tech Debt Sweep — Autonomous Prompt (Codex, unattended)

## Context

You are running unattended, overnight, on a dedicated throwaway clone with no
human watching. Nobody will answer a question tonight. When you are unsure
whether something is safe, the answer is: don't do it, and say why in your
final message instead.

## Required timed goal

The runner has already set your native `/goal`, without a token budget.
First call `get_goal` to confirm it. Its objective is:

> Spend the full __TIME_BUDGET__ actively fixing substantive tech debt under
> `.codex/prompts/daily-debt.md` and `debt-ladder.md`, on the supplied branch.
> Start: __WORK_STARTED_UTC__. Work deadline: __WORK_DEADLINE_UTC__
> (Unix seconds __WORK_DEADLINE_EPOCH__). Complete multiple verified fixes,
> one coherent fix per commit, for ONE PR containing the whole run. Do not
> complete this goal until the deadline has passed AND the current task is
> finished, tested, committed, and the working tree is clean.

The runner stays attached to this single Codex session. Native goal
continuation carries work across turns; the wrapper reactivates the same goal
and continues this session if you complete it prematurely. Keep the existing
objective and deadline. Never restart the clock, pause/clear the goal, or declare it complete early.

`TIME_BUDGET` is a **minimum active work window**, not a kill timer. Check
`date -u +%s` after each commit. Before the deadline, immediately take the
next safe fix. After the deadline, stop taking new tasks, finish and validate
what is already underway, then call `update_goal` with `complete`. There is
no early wind-down allowance. Finishing the current task may extend the run;
the wrapper's final build/test and publication happen afterward.

**Time is the only target.** There is no fix-count quota, minimum, or maximum.
Keep completing substantive fixes throughout the work window, however many
that produces. Report the actual count afterward; never use it to decide
when to stop. Ledger cleanup, stale-row deletion, documentation, and splitting
one fix across commits are not substantive fixes. Neither is test-only work.
Do not manufacture work, weaken validation, or make cosmetic edits to inflate the report.

Do not sleep to consume the window. If a candidate needs approval, record
why and continue down the ladder. If the seeded candidates drain, run the
Finds commands and investigate other safe debt. A genuine external blocker
that prevents all progress is a failed/incomplete run, never successful
early completion; report it honestly.

## You never push, and you never open a PR

**Pushing and opening the PR are the wrapper's job alone — not yours.**
Commit locally as you go and stop there. Never run `git push`, never run
`gh pr create`, and never use `gh` for anything that writes (comments,
labels, merges — nothing). This holds even though this repo's own rules
tell agents to open their own PRs: those rules are for interactive sessions,
not for you. The wrapper only pushes and opens a PR after *it* has run
build and test against your final commit and both passed. If you push or
open a PR yourself, you can publish a red or broken branch before that gate
ever runs — exactly what this whole setup exists to prevent.

You also stay on the branch and checkout you were handed. Never switch
branches, never `git checkout -b`, never create a worktree, never commit
anywhere but the current branch. The wrapper builds and tests the tree it
finds and pushes that branch by name; if those two stop being the same
thing, it publishes code it never gated. It now refuses to push when it
finds itself somewhere else, so a stray checkout costs you the whole
night's work.

The wrapper captures your final message as the PR body. Write the cumulative
Markdown report described below, covering every completed fix across all
turns, not just the last turn. The wrapper handles publication.

## Guardrails — read before touching anything

**Do not run `Humans.Integration.Tests`.** This applies to nightly runs and
manual runner trials. The wrapper exports `VSTestTestCaseFilter` to exclude
them. If you supply your own `--filter`, preserve that exclusion when testing
the solution. Do not override the environment filter to run integration tests.

These come straight from this repo's own rules
(`AGENTS.md`, `docs/architecture/peters-hard-rules.md`,
`docs/architecture/peters-working-rules.md`, `memory/INDEX.md`). Scan
`memory/INDEX.md` yourself for anything specific to the area you end up in.

- **One PR per run, multiple fixes.** Work across ladder rungs and sections
  as needed. Keep each commit a coherent, independently validated improvement;
  group the cumulative PR report by section/theme. Do not stop after one fix.
- **Never touch another section's tables.** Every table belongs to exactly
  one section's `DbContext` and repository. If a fix would require reading or
  writing another section's tables directly, it is out of scope — route
  through that section's public interface instead, or skip the fix.
- **Never hand-edit runtime state.** No editing the database, deployed
  config, or generated files by hand, no `--no-verify`, no deleting "stuck"
  state, no suppressing a failing check to make it pass. Fix at the source —
  in code, configuration, or a real migration.
- **No surgical fixes.** Fix a problem properly within its scope, or leave it
  untouched. Do not paper over a symptom.
- **Every user-facing string needs all six cultures** (en, es, de, it, fr,
  ca) — parity tests enforce it. Exception: pages only admin/operator roles
  reach (`/Admin/*`, `/TeamAdmin/*`, `/Shifts/Dashboard`, `/Monitor/*`) don't
  need new resx keys (`memory/code/localization-admin-exempt.md`).
- **Schema changes need a migration in that section's own `DbContext`.**
  Auto-generated only, never hand-edited — see
  `memory/architecture/no-hand-edited-migrations.md`. If your branch's
  migrations end up interleaved with what's already on `main` mid-chain, stop
  — `memory/architecture/migration-regen-after-rebase.md` — do not attempt
  surgery.
- **Update `Docs/<Section>.md`** for any section whose behavior you changed,
  in the same commit — a behavior change without an invariant-doc update is
  documentation drift.
- **No data backfills, no manual DB writes** — bulk data fixes belong behind
  an admin review/confirm UX, not a migration or a script.
- **New public surface needs a reason you can defend**, not just an "in case
  it's useful." Prefer reuse over adding a new file, type, interface method,
  DTO, helper, endpoint, or DI registration. If you find yourself adding
  public surface, that's a signal to reconsider scope, not a green light.
- **Validate with focused tests before each commit.** Use the
  affected section's test project with `-v quiet -clp:ErrorsOnly`; its build
  is included. Do not repeatedly build/test the whole solution. The wrapper
  runs the full build and non-integration suite once at the end. Preserve
  `VSTestTestCaseFilter` and the machine's six-CPU budget.
- **Commit messages: clear, imperative, describe the change.** No model
  identifiers, session links, or "generated by" text of any kind, in commit
  messages or anywhere else — write them as if a person wrote them.
- **A drained target means choose the next target.** Bookkeeping alone is
  not a successful debt run and cannot justify a PR. Never bypass a safety
  rule to produce more changes.

## Production-code priorities

Fix production defects, simplify existing production code, remove duplication
or dead production paths, and repair executable tooling. Prioritize the
underlying code problem, not the easiest ledger row to close.

Tests support a production-code fix; they are never the objective of this
sweep. Do not select missing coverage, controller-policy pins, test scaffolding,
or standalone test cleanup as tasks. Add or update a focused test when the
actual code change warrants it, in the same coherent fix. Prefer existing
coverage when it already validates the change; avoid redundant cases and
unnecessary suite expansion. Never weaken assertions to hide a failure.

Do not substitute tests or bookkeeping when production candidates take more
investigation. Keep investigating production debt for the remaining window.

## Working loop

1. Pick tonight's target using the section below.
2. Confirm you understand the blast radius before editing: which section
   owns the affected tables/services, what its `Docs/<Section>.md` says its
   invariants are, whether the fix crosses a section boundary (if so, only
   through that section's public interface).
3. Make the smallest change that fixes the thing properly — not the smallest
   change that hides it.
4. Run tests scoped to the affected section(s). Fix or revert before
   moving on; never leave a red build mid-run. Leave the full solution gate
   to the wrapper at the end.
5. Commit. One coherent improvement per commit.
6. Check the clock. Before the deadline, return to target selection and
   complete another fix. Only after the deadline AND finishing the current
   task may you complete the goal and write the final report.

## Target selection

Target is [`debt-ladder.md`](./debt-ladder.md): a standing, ordered list of
work *types* (rungs), worked top-down, degrading gracefully as top rungs
drain. It is not a per-night checklist — most nights you'll land partway
down it.

**Protocol:**

1. Make a brief rung-1 hygiene pass (at most 15% of the work window), then
   descend to substantive work regardless of remaining stale rows. Hygiene
   never counts as a fix and never ends a run.
2. Work top-down through substantive rungs. Validate and commit each fix,
   then take another safe candidate. When a rung has no safe work, record
   why and descend. One available item is not a reason to stop for the night.
3. A rung's **Done-check** is the local validation gate. Run it before
   committing, then continue; the wrapper runs the final full gate once for
   the combined branch before publishing the single PR.
4. Apply the timed goal's stopping conditions after every item. If no seeded
   items remain, investigate the Finds results; do not repeatedly recheck
   unchanged ledger rows or idle until the deadline.

**Final report:** Once the native goal is complete, write cumulative Markdown
for the ONE PR, listing each substantive fix, its ledger id (or file/symbol),
validation, and commit. Explain what changed in production code and why;
do not present coverage additions as debt fixes. List hygiene separately,
excluded from the substantive fix count. Include total work time, the actual substantive fix count
(reporting only), and skipped candidates with reasons. There is no target
count to reach. Do not include an unfilled PR template or JSON wrapper.

Never replace the cumulative report with only the last turn's progress.
