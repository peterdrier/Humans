# Daily Tech Debt Sweep — Autonomous Prompt (Codex, unattended)

## Context

You are running unattended, overnight, on a dedicated throwaway clone with no
human watching. Nobody will answer a question tonight. When you are unsure
whether something is safe, the answer is: don't do it, and say why in your
final message instead.

You have a **__TIME_BUDGET__ wall-clock budget** for this run. The wrapper
enforces this with SIGINT at the cap (giving you a chance to wind down
cleanly), followed by SIGKILL 120 seconds later if you have not exited.
Treat SIGINT as the backstop, not the plan: **reserve the last
~__WIND_DOWN_MINUTES__ minutes of your budget to land a clean, green,
committed state** — stop picking up new work well before the cap, finish
what you're mid-way through or revert it, run the section's tests, commit,
and write your final message (see "Target selection" below for what it must
contain). A partial, green, committed change is the goal. A rushed,
half-finished change that gets SIGKILLed mid-write is a failed run.

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

Since you never call `gh`, you cannot write the PR body yourself either.
Instead, the wrapper runs you with `--output-last-message`, so **your final
message becomes the PR body**: make sure it's the report described in
"Target selection" below before you finish, not a status update to nobody.

## Guardrails — read before touching anything

These come straight from this repo's own rules
(`AGENTS.md`, `docs/architecture/peters-hard-rules.md`,
`docs/architecture/peters-working-rules.md`, `memory/INDEX.md`). Scan
`memory/INDEX.md` yourself for anything specific to the area you end up in.

- **One coherent theme per run.** Pick one target and stay in it. Do not
  scatter small unrelated fixes across sections — that produces a PR nobody
  can review quickly, which defeats the point of an unattended run.
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
- **Build and test before every commit**, scoped to the change's blast
  radius per `memory/process/scoped-inner-loop-tests.md`
  (`dotnet build Humans.slnx -v quiet -clp:ErrorsOnly`, `dotnet test Humans.slnx -v quiet -clp:ErrorsOnly` at
  minimum for anything cross-section).
- **Commit messages: clear, imperative, describe the change.** No model
  identifiers, session links, or "generated by" text of any kind, in commit
  messages or anywhere else — write them as if a person wrote them.
- **If you cannot find safe work inside tonight's target, exit without
  committing.** Do not invent busywork, do not reach outside the target to
  find something to do, do not make a token change just to have something to
  show. No commit is a valid, successful outcome — the wrapper treats it as a
  quiet no-op, not a failure.

## Working loop

1. Pick tonight's target using the section below.
2. Confirm you understand the blast radius before editing: which section
   owns the affected tables/services, what its `Docs/<Section>.md` says its
   invariants are, whether the fix crosses a section boundary (if so, only
   through that section's public interface).
3. Make the smallest change that fixes the thing properly — not the smallest
   change that hides it.
4. Run the section's own tests, then the cross-cutting gate if you touched
   shared surface. Fix or revert before moving on; never leave a red build
   mid-run.
5. Commit. One coherent improvement per commit.
6. Watch the clock. With ~__WIND_DOWN_MINUTES__ minutes left, stop starting
   anything new, make sure the working tree is clean and green, write your
   final message, and end your turn.

## Target selection

Target is [`debt-ladder.md`](./debt-ladder.md): a standing, ordered list of
work *types* (rungs), worked top-down, degrading gracefully as top rungs
drain. It is not a per-night checklist — most nights you'll land partway
down it.

**Protocol:**

1. Start at rung 1. Run its **Finds** command. If it returns live,
   in-scope, safe work, do it — one item, one theme, per this file's
   guardrails — and stop there for tonight.
2. If a rung's Finds command returns nothing (or only work its own
   **Drained when** condition excludes, e.g. blocked on Peter's approval),
   say so and move to the next rung. Don't skip a rung with available work
   just because a lower one looks more interesting.
3. Rungs 1 and 10 never fully drain (recurring by design) — still try lower
   rungs after a quick pass on them if time remains.
4. A rung's own **Done-check** is the local test/build gate; run it before
   moving to the next item or rung.
5. If every rung is drained or every candidate needs a skip, exit without
   committing — that's success, not failure.

**Your final message must state:** which rung you worked and why (rung N+1
only after confirming rung N had nothing safe), which items you closed
(their ledger `id:` — every row has a permanent one, and quoting `what:`
instead breaks on any row you narrowed, since narrowing rewrites that text —
or `file:line` for debt that has no ledger row), everything you skipped
with the reason (public surface, Peter's-call, blocked, out of budget), and
any rung you descended past and why. Only needed when you commit — a no-op
night has no PR to fill. The wrapper folds your final message into the PR
body verbatim; write it as the PR body's own content, not a message to the
wrapper.
