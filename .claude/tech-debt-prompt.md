# Tech Debt Reduction — Claude Autonomous Prompt

## Mission

One autonomous pass of real tech-debt reduction on the Humans codebase. You find the
work yourself — there is no frozen backlog in this file, and none should ever be
added to it.

## Read first — the canon, in order

1. `AGENTS.md` — architecture, glossary, build/test commands, PR rules.
2. `docs/architecture/peters-hard-rules.md` and `peters-working-rules.md` — the
   constitution; final word on any conflict.
3. `.codex/TECH_DEBT_QUEUE.md` — the shared current-state file for tech-debt passes:
   canonical debt sources, priority order, hard boundaries, loop protocol. Resume
   from its *Current state* section; rewrite (never append to) that section as you go.

## Where debt lives (pointers, not copies)

- Live architecture baselines: `tests/Humans.Web.Tests/Architecture/Baselines/` —
  regenerate counts with the command in the queue file.
- Debt ledger: `docs/architecture/debt-ledger.yml` + per-section
  `src/Sections/Humans.<X>/Docs/debt.yml`. Respect `parked:`. New findings go in the
  ledger (`memory/process/debt-ledger-additions.md`), never into a prompt file.
- The section model — what a section *should* look like:
  `docs/sections/SECTION-TEMPLATE.md`, `docs/architecture/design-rules.md`, each
  section's `Docs/<Section>.md` + `Docs/data-access.md`. Divergence from the model is
  the debt.
- Surface / coupling baseline: build, then `reforge surface-score --all` per the
  queue file. Reduce it through architecturally-real deletions; the score is a
  detector, never the objective.

## Working rules

- Launched via `.claude/run-autonomous.sh`, your branch and worktree already
  exist — the wrapper's preamble says "Use the current git branch"; stay in that
  checkout. Invoked directly, create branch `techdebt/YYYY-MM-DD-claude-N` in a
  worktree at `.worktrees/<same-name>` off `origin/main`.
- One coherent improvement per commit. Targeted section tests + build per change;
  full `dotnet test Humans.slnx -v quiet` before any push. `-v quiet` always.
- Honor every boundary in the queue file's *Boundaries* section — especially: debt
  only (never feature follow-ups), no authorization/privacy shape changes, no
  reverting documented test-infrastructure decisions, new public surface goes to
  *Needs Peter* instead of into the code.
- Scan `memory/INDEX.md` when unsure whether a rule applies.
- Push and open a PR yourself (`memory/process/always-open-a-pr.md`); finish by
  rewriting the queue file's *Current state* (baseline counts, surface baseline,
  blocked items, *Needs Peter*) in the same PR.
