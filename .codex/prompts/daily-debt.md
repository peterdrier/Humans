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
> one coherent fix or batch per commit, for ONE PR containing the whole run.
> Do not complete this goal until the deadline has passed AND the current task is
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

**The ledger is the target; time is the only stopping rule.** The debt
ledgers — `inbox:` in `docs/architecture/debt-ledger.yml` plus `inbox:` in every
`src/Sections/*/Docs/debt.yml` — are a holding bucket, not an archive. **Every
run must end with fewer open rows than it started with; a run whose ledger did
not shrink has failed**, however much else it fixed. Count open rows at the
start (`git grep -hE '^\s+(- )?id: [A-Z][A-Z0-9]*-[0-9]+\s*$' HEAD -- docs/architecture/debt-ledger.yml 'src/Sections/*/Docs/debt.yml' | wc -l`);
the wrapper counts again at the end and publishes a run that did not shrink
the ledger as a draft marked failed.

Fixing the code a ledger row describes and deleting that row is a
substantive fix — the most valuable kind. Deleting a row whose defect the code
already fixed shrinks the ledger but is hygiene, not a fix. Documentation,
test-only work and splitting one fix across commits are not substantive fixes.
There is no fix-count quota; never use a count to decide when to stop. Do not
manufacture work, weaken validation, or make cosmetic edits to inflate the report.

Do not sleep to consume the window. If a ledger row truly cannot be fixed
autonomously — it needs Peter's approval, new public surface or a policy
decision — leave it, list its id under **Needs Peter** in the report with the
one decision that would unblock it, and take the next row. The morning debt
review elevates the most important of these to GitHub issues (at most three a
night) and removes their rows, so order the list most important first. Only when
no ledger row is actionable tonight, run the Finds commands for new debt.
A genuine external blocker
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
- **Validate with focused tests before each commit, once per batch.** Run
  each affected section's test project with `-v quiet -clp:ErrorsOnly`; its
  build is included. Include applicable rung Done-checks and architecture
  checks for multiple sections, contracts, Base/Shell, or unclear scope.
  Deduplicate checks shared by batch members, not coverage. Do not repeatedly
  build/test the whole solution. The wrapper runs the full build and non-integration suite once at the end. Preserve
  `VSTestTestCaseFilter` and the machine's six-CPU budget.
- **Commit messages: clear, imperative, describe the change.** No model
  identifiers, session links, or "generated by" text of any kind, in commit
  messages or anywhere else — write them as if a person wrote them.
- **A drained target means choose the next target.** Bookkeeping alone is
  not a successful debt run and cannot justify a PR. Never bypass a safety
  rule to produce more changes.

## Production-code priorities

Close existing ledger rows before hunting new debt. Fix each row's underlying
code problem properly — never close a row by rewording it, narrowing it to
nothing, or moving it to another ledger. Beyond the ledger: fix production
defects, simplify existing production code, remove duplication or dead
production paths, and repair executable tooling.

**Adding a row is a cost against the run's goal.** Fix what you find when it is
safe to. Add a row only for a real defect you cannot fix unattended, and only
when the run's closures already outnumber its additions — the net must stay
negative. Do not record approval-blocked ideas, design preferences or "looked
at it" notes as rows; put them under **Needs Peter** in the report instead.

Tests support a production-code fix; they are never the objective of this
sweep. Do not select missing coverage, controller-policy pins, test scaffolding,
or standalone test cleanup as tasks. Add or update a focused test when the
actual code change warrants it, in the same coherent fix. Prefer existing
coverage when it already validates the change; avoid redundant cases and
unnecessary suite expansion. Never weaken assertions to hide a failure.

Do not substitute tests or bookkeeping when production candidates take more
investigation. Keep investigating production debt for the remaining window.

## Working loop

1. Pick tonight's target using the section below. For a simple correction,
   collect a small batch using the batching rules below before editing.
2. Confirm each candidate and the combined blast radius: which section owns the affected tables/services, what its `Docs/<Section>.md` says its
   invariants are, whether the fix crosses a section boundary (if so, only
   through that section's public interface).
3. Make the smallest change that fixes the thing properly — not the smallest
   change that hides it.
4. Run scoped tests and applicable Done-checks once against the completed
   batch (or individual fix). Resolve failures before committing or moving
   on. If a candidate is deferred, validate the remaining diff again.
   Leave the full solution gate to the wrapper at the end.
5. Commit. One coherent improvement or batch per commit; no per-file commits
   for repeated simple corrections.
6. Check the clock. Before the deadline, return to target selection and
   complete another fix. Only after the deadline AND finishing the current
   task may you complete the goal and write the final report.

## Batching simple corrections

Batch verified instances of the same correction instead of cycling through
search, edit, test, and commit for every file. For example, localize several
strings together, then fix several equivalent cancellation boundaries in a
separate commit. Same-theme batches may span sections through existing public
interfaces; section ownership still applies to every candidate.

- Start from a live candidate and use the rung's Finds command plus nearby
  call sites to collect related instances. Keep discovery brief and the batch
  small enough to review and finish as one task; do not inventory the whole
  repository or wait for a minimum batch size. Preserve ladder priority so
  cheap cleanup frees time for substantial production work.
- Verify every instance against current code before accepting it. Group by
  the same correction and compatible risk, not just a filename, analyzer,
  or broad theme. Keep unrelated corrections in separate commits.
- Localizations still need the admin/operator exemption checked per route,
  existing keys reused where appropriate, and all six cultures for every
  string. Run resource parity once for the batch, plus affected section tests.
- Cancellation changes require inspecting token origin and call semantics
  at every site. Read-only calls and external mutations follow different
  rules (`memory/architecture/cancellation-token-propagation.md`); never
  blanket-forward or blanket-detach tokens across unlike boundaries.
- Keep migrations, authorization changes, public-contract changes, complex
  behavior changes, and candidates needing approval or deeper investigation
  out of simple batches. Handle eligible work separately under its existing
  rules; skip approval-dependent work in this unattended run.
- Fix failures and review the whole diff before committing. Do not shrink
  verification to one representative file or section. A substantive
  individual refactor remains a valid task; batching is for repeated simple
  corrections, not a reason to avoid harder work.

Freeze batch membership before editing. Check the clock during discovery;
when the deadline passes, stop adding candidates and finish only the current
batch. Report each corrected item and its ledger id/file under the shared
commit and validation, so fewer commits do not hide what changed.

## Optional Luna helpers

The coordinator runs on `gpt-6.1-sol` with medium reasoning by default. You
may spawn `gpt-6-luna` subagents when a bounded task saves enough work to
justify briefing and reviewing it. Delegation is optional, never a quota.

- Good tasks: gathering candidate locations, finding existing resource keys,
  preparing localization batches, summarizing long logs, or applying an
  already-decided mechanical correction to explicitly assigned files.
- Give a narrow brief with paths, the exact correction, relevant repo rules,
  expected output, and the work deadline. Use a fresh context (`fork_turns:
  "none"`); the runner configures helpers to use `gpt-6-luna` with medium
  reasoning. Do not override that routing or copy the whole session for a
  small task; model/effort parameters are not required on the spawn tool.
- Prefer read-only helpers. For edits, assign disjoint files (including tests
  and resx files) and do not edit those files concurrently. Start with at most
  two helpers; do tiny tasks directly rather than spawning per string/file.
- Helpers never build, test, commit, push, open PRs, switch branches, create
  worktrees, or manage the coordinator's goal. Keep compiler work serial and
  the shared checkout on the supplied branch. Finish all helper work before
  validation, commit, and goal completion; stop adding work at the deadline.
- Keep task selection, cancellation semantics, architecture and authorization
  decisions, final diff review, validation, and commits with the coordinator.
  Verify helper findings against code and review every edit; summaries alone
  are not proof. Validate the integrated batch once under the existing rules.
- If Luna or subagent tools are unavailable, do the work directly. Do not
  change the coordinator model or spend the work window repairing delegation.

## Target selection

Target is [`debt-ladder.md`](./debt-ladder.md): a standing, ordered list of
work *types* (rungs), worked top-down, degrading gracefully as top rungs
drain. It is not a per-night checklist — most nights you'll land partway
down it.

**Protocol:**

1. Count the open ledger rows (command above) and note the number.
2. Make a brief rung-1 hygiene pass (at most 15% of the work window), then
   descend to substantive work regardless of remaining stale rows. Hygiene
   shrinks the ledger but never counts as a fix and never ends a run.
3. Work the open ledger rows: oldest `added:` first, using the rungs' order
   and mechanics when several are workable. Validate and commit each fix or
   simple batch, delete the closed rows in the same commit, then take another.
   Skip a row only when it needs approval or a decision (list it under
   **Needs Peter**) or cannot be fixed safely unattended (say why).
4. Only when no ledger row is actionable, work the rungs' Finds commands for
   new debt. One available item is not a reason to stop for the night.
5. A rung's **Done-check** is the local validation gate. Run it once per
   completed batch or individual fix before committing, then continue. The
   wrapper runs the final full gate once before publishing the single PR.
6. Apply the timed goal's stopping conditions during batch discovery and
   after every commit. If no seeded items remain, investigate the Finds results; do not repeatedly recheck
   unchanged ledger rows or idle until the deadline.

**Final report:** Once the native goal is complete, write cumulative Markdown
for the ONE PR. Lead with the ledger: open rows at start → end, the ids
closed, the ids added (each with why it could not be fixed tonight), and the
**Needs Peter** list. Then list each substantive fix, its ledger id (or file/symbol),
validation, and commit. Explain what changed in production code and why;
do not present coverage additions as debt fixes. List hygiene separately,
excluded from the substantive fix count. Include total work time, the actual substantive fix count
(reporting only), and skipped candidates with reasons. There is no target
count to reach. Do not include an unfilled PR template or JSON wrapper.

Never replace the cumulative report with only the last turn's progress.
