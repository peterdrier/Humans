# Debt Sweep — Design

**Date:** 2026-06-12
**Status:** Approved by Peter (design dialogue, this date)
**Skill:** `.claude/skills/debt-sweep/SKILL.md`

## Problem

Tech-debt cleanup exists (`refactor-swarm`, `section-read-split`, `section-align`, baselines, ratchets, `[Grandfathered]`) but every tool needs Peter to point it. There is no autonomous daily picker analogous to `/freshness-sweep` — something that knows where the debt pile is, takes a fair bite, fixes it, and asks a few questions at the end. Left to gravity, every pass targets the biggest section (Users); the picker must rotate fairly and must be able to fix cross-section issues on both sides at once.

## Decisions (from design dialogue)

| Question | Decision |
|---|---|
| Per-sweep shape | **Themed bite** — one debt class per run, one coherent PR. Mixed-bag PRs are harder to review. |
| Inventory cost | **No full scan per run.** A committed ledger carries rotation state; each run does one cheap `detect` for the chosen theme plus a cheap staleness check. |
| Rotation granularity | **By debt class only** (no class × section matrix). Within a theme, items in recently-served sections are worked last, via a `recent_sections` list in the ledger. |
| Bite size | **Time/token budget, not item/diff count**: work the theme ~2h (default, `--budget` dial) or until drained, whichever first. |
| Review gate | **Per-theme `review:` field.** `light` = build + tests + forbidden-move grep. `panel` = additionally a second-opinion reviewer subagent gates each commit (refactor-swarm-type judgment changes). |
| Seeding | Ledger seeded during skill implementation (judgment calls made with Peter), not on first run. |
| Ad-hoc discoveries | Ledger `inbox:` list — any session appends one-off items; recurring classes become new theme entries. |
| Inbox scheduling | Inbox is itself a theme in normal rotation; inbox items matching the chosen theme ride along with it. |
| DB schema changes | **Fully out of scope** — the sweep never produces an EF migration. DTO/class property drops are fine; schema-touching work is noted and left alone. |
| Drained themes | Retire (delete) the entry when an enforcer guards regression; otherwise keep at `remaining: 0`, re-checked only by `--inventory`. |

## Invocation

```
/debt-sweep [--budget=2h] [--theme=<id>] [--inventory]
```

| Flag | Behavior |
|---|---|
| *(none)* | rotation picks the theme; ~2h budget |
| `--budget=<duration>` | override the time/token budget (e.g. `4h`, `30m`) |
| `--theme=<id>` | skip rotation, work this theme (directed run) |
| `--inventory` | full re-scan: re-run every theme's `detect`, merge newly-found classes, refresh counts, evict entries whose debt is gone. May be combined with a normal sweep or run standalone. |

## The ledger — `docs/architecture/debt-ledger.yml`

Committed YAML; the rotation state between runs (the freshness-anchor analog). Schema:

```yaml
version: 1
recent_sections: [Users, Teams]   # dominant sections of last ~3 sweeps; item-picking deprioritizes
themes:
  - id: grandfathered-hum0017
    title: Cross-section full-service injections
    detect: "grep -rl 'Grandfathered(\"HUM0017\"' src/ | wc -l"   # cheap, returns remaining count
    review: light          # light | panel
    last_swept: never      # or YYYY-MM-DD
    remaining: 9           # snapshot from last visit / inventory
    notes: ""              # constraints, explicitly-deferred items
inbox:
  - added: 2026-06-12
    what: "ViewComponents injecting IMemoryCache directly (3 sites)"
    review: light
```

Rules:

- **Rotation:** oldest `last_swept` wins (`never` sorts first); the chosen theme's `detect` is re-run to confirm `remaining > 0` before committing to it. If 0, update the entry and pick the next-oldest.
- **Fairness:** within the chosen theme, items living in `recent_sections` are worked last. After the sweep, `recent_sections` is updated to the dominant sections of the last ~3 sweeps.
- **Adding a theme:** append an entry with `last_swept: never` — rotation serves it next automatically. Any session may do this when a recurring debt class surfaces (convention captured as a `memory/` atom).
- **Adding an inbox item:** append to `inbox:` — for one-off discoveries that don't constitute a class.
- **Retirement:** when a theme drains to 0 *and* a structural enforcer now guards regression (analyzer at Error with no remaining grandfathers, architecture test with empty baseline), delete the entry and note it in the report. The staleness check re-creates it if the debt ever reappears. Themes with no enforcer stay at `remaining: 0`.

## Run phases

Skeleton mirrors `/freshness-sweep` (worktree → work → report → PR → inline questions → teardown).

### Phase 0 — Setup

`REPO_ROOT=$(git rev-parse --show-toplevel)`. Parse `--budget` (default 2h); record the start time. Budget is wall-clock guidance for the work loop, checked between items — never mid-item.

### Phase 1 — Worktree

```bash
git fetch origin main
TS=$(date -u +%Y-%m-%dT%H%M%SZ)
git worktree add $REPO_ROOT/.worktrees/debt-sweep-$TS -b debt-sweep/$TS origin/main
```

Scope is frozen at the branch point, same doctrine as freshness Phase 2: never re-fetch or reconcile mid-run; anything landing after is the next sweep's input.

### Phase 2 — Staleness check (every run, cheap)

1. Distinct `HUM####` IDs carrying `[Grandfathered]` in `src/` vs. ledger theme ids → missing rules become new themes (`last_swept: never`, `review: light` unless obviously judgment-class).
2. Files in `tests/Humans.Application.Tests/Architecture/Baselines/` vs. ledger → same.
3. New themes are recorded in the ledger update (Phase 5) even if not worked this run.

With `--inventory`: additionally re-run **every** theme's `detect`, refresh all `remaining` counts, evict entries whose debt is gone (with report note), and scan for debt classes the seed missed.

### Phase 3 — Pick theme

`--theme` if given; otherwise rotation per the ledger rules. Enumerate the theme's concrete items (the `detect` command's file list, baseline lines, warning sites, etc.), order them with `recent_sections` last, and fold in any matching `inbox` items.

### Phase 4 — Work loop

Until budget exhausted or theme drained, per item:

1. Fix it properly (no surgical fixes — constitution).
2. `dotnet build Humans.slnx -v quiet`.
3. Targeted tests (section tests + `Architecture` tests; full `dotnet test` at least before each push).
4. **Forbidden-move grep** on the diff: `#pragma warning disable HUM`, `[SuppressMessage`, *new* `[Grandfathered]`, `// ReSharper disable`, visibility changes that dodge a rule rather than fix it. Any hit → revert the item, record in report.
5. `review: panel` themes only: a second-opinion reviewer subagent (opus-tier, score-blind, default-reject posture per refactor-swarm) judges whether the fix is a good idea, not merely green. Reject → revert or rework once; persistent reject → skip item, record.
6. Commit (one item or one tight cluster per commit). Push every 3–5 items (`push-often-during-long-runs`).

Off-theme debt discovered while working → append to `inbox`, never chased. Items needing Peter (interface additions, schema work, privilege changes) → skip, record as an end-of-run question.

### Phase 5 — Ledger update + report

In the worktree: set the theme's `last_swept`, re-run `detect` for the new `remaining`, update `recent_sections`, add staleness-check discoveries, append work-loop inbox items, apply retirement/eviction. Overwrite `docs/debt/last-report.md`: timestamp, theme, items fixed (one line each), items skipped + why, forbidden-move reverts, inbox additions, ledger changes. Commit both with the work.

### Phase 6 — PR

```
git push -u origin debt-sweep/$TS
gh pr create --repo peterdrier/Humans --base main --title "debt: <theme title> — N items" ...
```

One PR per sweep. Body: theme, per-item bullets, ledger delta, link to report.

### Phase 7 — Inline question round (freshness Phase 7.5 doctrine, verbatim)

Every judgment call, skipped item, and uncertainty is delivered **inline in chat** as a terse numbered list — the report is the record, not the delivery channel; assume Peter never opens it. Wait for answers, apply edits, **commit and push** (a bare push after editing sends nothing). Zero questions → say so in one line.

### Phase 8 — Teardown

Only after Phase 7 resolves: `git worktree remove` (never `rm -rf`). Branch stays on origin until the PR closes.

## Standing constraints (baked into the skill)

- **No EF migrations, ever.** The sweep never changes schema. DB column/table drops, index changes, conversions that touch the model snapshot — all out of scope; note and move on. Dropping a property from a DTO/view model/non-entity class is fine.
- **Never touch `[DontFix]`** — Peter-applied permanent exceptions; tech-debt passes skip them.
- **No analyzer suppressions** in any form (`no-analyzer-suppressions`).
- **Interface additions stop-and-ask** (`interface-method-additions-are-debt`) — in practice: skip the item, raise in Phase 7.
- **No data migrations / backfills** (`no-data-backfills`, `feedback_never_author_data_migrations`).
- **Explicit subagent models:** sonnet for mechanical fix workers, opus-tier for panel reviewers; every agent name/description tags its model.
- Sweep touches only: theme item files, the ledger, and `docs/debt/last-report.md`.

## Seed inventory (initial themes)

Built during implementation with Peter; the table below was the starting shape — final ids, detect commands, and counts live in `docs/architecture/debt-ledger.yml` (seeded from `origin/main @ e3fdfbcab`, 2026-06-12).

**Seed outcomes that differ from the table:**
- **HUM-coded build warnings are not a separate theme.** A clean build's 97 warnings are entirely HUM0024 / HUM0028 / HUM0031 / HUM_USER_DISPLAYNAME — the grandfathered themes surfacing. Warning-backed themes use a shared `detect: build:<CODE>` sentinel counted from one build log per run. A `build-warnings-misc` catch-all theme (seeded at 0) owns any future non-HUM warnings (compiler CS, NuGet NU, framework obsoletions).
- **`NoLinqAtDbLayer` and `NoStartupGuards` baselines were already empty** — drained before the skill existed; retired per the drain rule, not seeded.
- **`NoDestructiveMigrationOps` baseline is excluded** — its entries are immutable migration history (a guard, not fixable debt).
- **`tech-debt-2026-04-23-leftovers` became inbox entries** rather than a theme — the doc mixes done/open items, so its open items were enumerated into the inbox once, and the final inbox entry retires the doc itself.
- Per-rule grandfathered themes seeded: HUM0024 (34 attribute sites), HUM0028 (17), HUM0031 (15 attributes / 26 warning sites). HUM0009, HUM0020, HUM0025, HUM0029, and HUM0032 had **zero** real attribute sites — an unanchored grep had counted analyzer doc-comments and message strings as debt (caught by Codex review on PR 989); those rules are already fully enforced and were not seeded.

| Theme | Source | Review |
|---|---|---|
| `grandfathered-hum####` (one theme per rule with sites) | `[Grandfathered]` attributes | light |
| `baseline-no-linq-at-db` | `NoLinqAtDbLayer.baseline.txt` | light |
| `baseline-entity-read-returns` | `ApplicationServiceEntityReadReturns.baseline.txt` | light |
| `baseline-display-sort` | `DisplaySortInControllers.baseline.txt` | light |
| `baseline-startup-guards` | `NoStartupGuards.baseline.txt` | light |
| `obsolete-cross-section-navs` | `[Obsolete]` navs + CS0618 pragma sites | panel — any item whose fix would change the model snapshot is schema work: skip, note |
| `invalidator-ratchet-hum0028` | grandfathered invalidator interfaces | panel |
| `controller-logic-hum0031` | controllers near the ratchet threshold | light |
| `cross-section-read-splits` | sections without `I…ServiceRead` consumed cross-section | panel |
| `repos-sharing-tables` | `SingleRepositoryPerTableAnalyzer` grandfathers | panel |
| `tech-debt-2026-04-23-leftovers` | open items in that doc | per-item |
| `inbox` | ledger inbox list | per-item |

## Relationship to existing skills

- **`refactor-swarm`** stays the heavyweight, Peter-directed, multi-lane score burndown. `/debt-sweep` is the daily autonomous single-theme bite; a `panel` theme is roughly one refactor-swarm lane's discipline without the swarm.
- **`section-read-split`** may be *invoked* by a sweep working the `cross-section-read-splits` theme rather than reimplementing it.
- **`freshness-sweep`** owns docs; `/debt-sweep` owns code debt. The skills share the worktree/report/PR/inline-questions skeleton deliberately.

## Failure modes

| Failure | Behavior |
|---|---|
| Ledger YAML parse error | Abort before worktree work; report |
| `detect` command fails | Skip theme, pick next; record in report |
| Item fix breaks build/tests and can't be righted | Revert that item, record, continue |
| Panel reviewer rejects twice | Skip item, record, continue |
| Budget hit mid-theme | Normal: commit what's done, ledger reflects remaining |
| Push / PR fails | Worktree retained; fix manually |
