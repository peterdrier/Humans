# Humans Tech Debt Queue

Current-state file for autonomous tech-debt passes. Resume here before doing new
discovery. **This file is rewritten in place — nothing here grows append-only.**
Run history lives in `git log -- .codex/TECH_DEBT_QUEUE.md`, not in this file.

## Mission

Shrink real architecture debt: live baseline entries, ledger themes, and measured
public surface / cross-section coupling. Real code improvements only — never hide a
violation, never edit a baseline the code still triggers.

## Canonical sources — never copy an inventory into this file

- **Live architecture baselines:** `tests/Humans.Web.Tests/Architecture/Baselines/`.
  Regenerate counts (do not trust any copy):

  ```powershell
  Get-ChildItem tests/Humans.Web.Tests/Architecture/Baselines -File | Sort-Object Name |
    ForEach-Object {
      $count = (Get-Content $_.FullName | Where-Object { $_ -and -not $_.TrimStart().StartsWith('#') }).Count
      [pscustomobject]@{ Name = $_.Name; Entries = $count }
    } | Format-Table -AutoSize
  ```

- **Debt ledger:** `docs/architecture/debt-ledger.yml` (global themes + inbox) and
  per-section `src/Sections/Humans.<X>/Docs/debt.yml`. Respect `parked:` — only Peter
  removes a park. Record newly-found debt there per
  `memory/process/debt-ledger-additions.md`, not here.
- **Section model (self-serve discovery):** `AGENTS.md`,
  `docs/architecture/peters-hard-rules.md`, `docs/architecture/design-rules.md`,
  `docs/sections/SECTION-TEMPLATE.md`, and each section's own
  `src/Sections/Humans.<Section>/Docs/<Section>.md` + `Docs/data-access.md`.
  Debt is whatever diverges from that model; find it by comparing a section against
  the model, not by consuming a frozen list.
- **Surface / interconnectivity baseline:** `dotnet build Humans.slnx -v quiet`, then
  `reforge surface-score --all --top-symbols 200 --format Json` (score a **built**
  solution — unbuilt under-reports ~4%). Rank sections by the Section Refactor History
  table in `docs/architecture/maintenance-log.md`. The score is a detector, not an
  objective — every change needs an architecture thesis that stands without score
  movement (`.codex/skills/humans-tech-debt/references/humans-tech-debt-rules.md`).

## Priority order

1. Live baseline entries — smallest real refactor that removes the violation; delete
   the baseline line only after the code no longer triggers the rule.
2. Unparked ledger themes, ledger inbox, per-section `debt.yml` items.
3. Surface and cross-section coupling reduction (reforge-guided, score-blind reviewed).
4. Self-serve discovery against the section model.

## Boundaries (hard — learned the expensive way, 2026-08-25 run)

- **Debt only, never features.** A "Follow-up" section in a feature spec is a feature,
  not debt — even when fully specced. Leave it; note it under *Needs Peter*.
- **Never change authorization or privacy shape** — role lists, search scopes, data
  visibility, consent gates. If debt work brushes one, stop and record it under
  *Needs Peter*.
- **Never revert a documented test-infrastructure decision** (e.g. integration-test
  parallelism) to stabilise a flake. Fix the shared dependency or file an issue; a
  slower suite is not a fix.
- **New public/interface surface needs Peter** — `memory/process/reuse-first-change-discipline.md`.
  Queue it under *Needs Peter* instead of adding it.
- **No schema, migration, entity-shape, or JSON-serialization changes.** Per-section
  `Migrations/**` are immutable history.
- **Fix at the source or file an issue** — no symptom patches
  (`docs/architecture/peters-hard-rules.md`).

## Loop protocol

1. Regenerate the baseline counts and (periodically) the surface baseline; update
   *Current state* below **by rewriting it**.
2. Pick one item by the priority order. Write a one-sentence architecture thesis; if
   the thesis is "a number goes down", pick something else.
3. Make the smallest real refactor. Targeted section tests + `dotnet build Humans.slnx -v quiet`
   per change; full `dotnet test Humans.slnx -v quiet` before any push.
4. One coherent improvement per commit; push the branch; open/refresh the PR
   (`memory/process/always-open-a-pr.md`).
5. When stopping: rewrite *Current state* (including *Needs Peter*), leave the
   worktree clean.

## Current state — rewrite this whole section every run; never append

*As of 2026-08-25, branch `techdebt/2026-08-25-codex-1` (curated into peterdrier/Humans#1514).*

- **Baselines:** counts live in the baseline files only — regenerate with the command
  above, never record them here (`memory/process/no-derived-aggregates-in-docs.md`).
  `NoDestructiveMigrationOps` is blocked by design — immutable migration history, not
  a backlog.
- **Remaining entity-read classification:** Auth `FindUserByVerifiedEmailAsync`
  (Identity `UserManager` needs the entity); Events moderation/camp/user event reads
  (validated read-then-mutate); Shifts settings/rota/shift/signup reads (need a
  command/read-model design, not a return-type swap); Teams `GetTeamByIdAsync` /
  `GetTeamEntityBySlugAsync` (admin authorization + mutation-adjacent state).
- **Surface baseline:** never recorded here — regenerate with `reforge surface-score`
  on a built solution (see above) at the start of each run
  (`memory/process/no-derived-aggregates-in-docs.md`); per-section history lives in
  the Section Refactor History table in `docs/architecture/maintenance-log.md`.
- **Needs Peter:** none open. (Legal-name picker scope shipped separately as
  peterdrier/Humans#1516; integration-test Serilog race filed as
  nobodies-collective/Humans#1145.)
