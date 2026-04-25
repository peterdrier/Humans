# Freshness Sweep — Design

**Date:** 2026-04-25
**Status:** Spec — pending review
**Author:** Peter Drier (with Claude)

## Problem

A growing list of files in this repository drift out of sync with the code they
describe. Examples:

- The About page (`Views/About/Index.cshtml`) lists production NuGet packages with
  versions and licenses. After every NuGet update it goes stale.
- `docs/sections/Teams.md` has a list of `SystemTeamIds` that must match
  `src/Humans.Domain/Constants/SystemTeamIds.cs`.
- `docs/development-stats.md` and `docs/reforge-history.csv` are generated from
  the codebase but only regenerate when somebody remembers to run the script.
- `docs/authorization-inventory.md`, `docs/controller-architecture-audit.md`,
  `docs/architecture/dependency-graph.md`, `docs/architecture/service-data-access-map.md`,
  `docs/guid-reservations.md`, and the data-model index in
  `docs/architecture/data-model.md` are inventories computed by hand and stale
  the moment a controller or service changes.
- The user-facing guide in `docs/guide/` is the highest-stakes case: when a UI
  control moves or a feature changes behavior, the corresponding guide page
  starts giving end users wrong instructions.

The combined risk is end users seeing stale public-facing content, and Peter
spending later effort to clean up batches of doc drift instead of preventing
it as it happens.

## Goal

Two deliverables:

1. **A definitive catalog** of every file in the repo that needs to stay in
   sync with code, with each entry declaring what triggers a refresh and how
   the refresh is performed.

2. **A project-specific Claude Code skill** (`/freshness-sweep`) that consumes
   the catalog, computes what changed since the last sweep, regenerates every
   objective artifact in place, and flags subjective ones for human review —
   producing a single PR per run.

## Non-goals

- Cron / scheduled execution. The skill is invoke-only at v1; cadence is
  manually driven (likely daily for diff mode, weekly for full-scan).
- Auto-merge. The skill opens a PR; merge stays a human decision.
- i18n / RESX drift. Tracked separately by the existing i18n-audit cadence.
- Replacing `docs/architecture/maintenance-log.md`. That doc tracks human
  cadence for things the skill does *not* automate (GDPR audits, screenshot
  reviews, etc.) and is hand-maintained.

## High-level architecture

Two artifacts ship together:

| Artifact | Role |
|---|---|
| `docs/architecture/freshness-catalog.yml` | Mechanical entries + list of editorial doc trees |
| Inline HTML-comment markers in editorial `.md` files | Per-doc trigger declarations and auto-update sub-blocks |
| `.claude/skills/freshness-sweep/SKILL.md` | The skill: reads catalog, computes diff, dispatches updates, opens PR |
| `docs/freshness/last-report.md` | Single, overwritten-per-run report committed alongside updates so it renders in PRs |

### Lifecycle of one diff run

1. **Resolve baseline.** `git fetch upstream main`. Find the most recent sweep
   commit on `upstream/main` via `git log upstream/main --grep='freshness sweep'
   --format=%H -n 1`. Extract the anchor hash from its commit message
   (`(upstream@<sha>)` token). If absent → fall back to full-scan and warn.
   Anchor hashes only ever live on `upstream/main` because the fork's `main`
   gets rebased on production promotion.

2. **Create worktree.** `git worktree add
   .worktrees/freshness-sweep-<timestamp> -b
   freshness-sweep/<timestamp> origin/main`. All work happens inside this
   worktree; the main checkout is untouched.

3. **Discover entries.** Parse `freshness-catalog.yml`. Walk every directory in
   `editorial_trees`; for each `.md`, parse inline markers. Validate uniqueness
   and well-formedness. Build an entry list.

4. **Match dirty entries.** Diff: `git diff --name-only
   <anchor>..upstream/main`. For each entry, glob-match changed paths against
   triggers. Build dirty list.

5. **Dispatch.** Group dirty entries into concurrent batches of ≤3 subagents
   (Peter's hard global cap). Each subagent owns one entry; its contract is to
   return `{updated: bool, files_changed: [], flags: [{section, reason}],
   questions: []}`. Mechanical entries with `update: script` invoke the script
   directly (no subagent).

6. **Aggregate.** Single commit if any updates: `docs: freshness sweep — N
   entries (upstream@<sha>)` with body listing entries touched and entries
   flagged. The commit *includes* the regenerated `docs/freshness/last-report.md`
   so the PR shows it in the file list and Peter can read it on github.com.

7. **Push + PR.** `git push origin freshness-sweep/<timestamp>`, then `gh pr
   create --repo peterdrier/Humans --base main` with the same title as the
   commit and a body summarizing the report.

8. **Tear down.** Skill returns the PR URL and removes the worktree with `git
   worktree remove`. If no updates were made, the worktree is removed quietly
   and no PR is opened.

### Full-scan mode

`/freshness-sweep --full` still performs `git fetch upstream main` (step 1) but
skips the anchor-resolution and the diff/match phases (step 4). Every catalog
entry is dirty. The commit message still records the `upstream/main` HEAD as
the new anchor so subsequent diff runs have a baseline. Otherwise identical
lifecycle. Used weekly as a sanity check; primary mode is diff.

### Inter-promotion behavior

When an earlier freshness sweep PR has merged to `origin/main` but not yet been
promoted to `upstream/main`, the next sweep finds the *previous* anchor on
`upstream/main` and reprocesses the same diff. Auto-updates are no-ops (files
already match), nothing commits, exit clean. Wasted CPU but correct output.

### Failure modes

| Mode | Behavior |
|---|---|
| No `upstream` remote configured | Skill errors out before doing any work |
| Catalog YAML parse error | Skill errors out before dispatching subagents |
| `freshness:auto` block missing closing tag | Skill flags doc as malformed, processes other entries |
| Two entries write the same target | Schema validation rejects this at parse time |
| Run fails mid-flight | Worktree left intact for inspection; no PR opened. Anchor only advances when a sweep commit lands on `upstream/main`, so next run re-processes |
| Trigger glob matches no real path | Schema validator warns (config bug, not runtime error) |

## The catalog

### `docs/architecture/freshness-catalog.yml`

```yaml
version: 1

# Mechanical entries — fully auto-derived, no human judgement.
mechanical:
  - id: about-page-packages
    target: src/Humans.Web/Views/About/Index.cshtml
    triggers:
      - "**/*.csproj"
      - "Directory.Packages.props"
    update: prompt
    prompt: |
      Read all .csproj files and Directory.Packages.props.
      Update the package list in About/Index.cshtml to match.
      Preserve descriptions; only update versions and add/remove
      packages as needed.

  - id: dev-stats
    target: docs/development-stats.md
    triggers: ["src/**/*.cs", "src/**/*.cshtml"]
    update: script
    script: docs/scripts/generate-stats.sh

  - id: authorization-inventory
    target: docs/authorization-inventory.md
    triggers:
      - "src/Humans.Web/Controllers/**/*.cs"
      - "src/Humans.Application/**/*.cs"
      - "src/Humans.Web/Authorization/**/*.cs"
    update: prompt
    prompt: |
      Regenerate from scratch. Find every [Authorize], RoleChecks.*,
      ShiftRoleChecks.*, and resource-based handler. Group by section.
      Match the existing table layout.

  # ... full list in §"Initial inventory" below

# Editorial doc trees — walked on every run; per-doc triggers from inline markers.
editorial_trees:
  - docs/sections/
  - docs/features/
  - docs/guide/
  - docs/architecture/coding-rules.md
  - docs/architecture/design-rules.md
  - docs/architecture/code-review-rules.md
  - docs/architecture/conventions.md
  - docs/seed-data.md

# Files explicitly outside the freshness regime.
# Two roles: (a) exclude files that WOULD be picked up by editorial_trees
# walks, (b) document deliberate non-coverage so future readers don't think
# something was forgotten. Files not in any tree don't strictly need to be
# listed but are included for clarity.
ignore:
  - docs/sections/SECTION-TEMPLATE.md          # inside walked tree, must exclude
  - docs/architecture/maintenance-log.md       # not walked; explicit for clarity
  - docs/architecture/tech-debt-*.md           # not walked; explicit for clarity
  - docs/architecture/screenshot-maintenance.md
  - docs/admin-role-setup.md
  - docs/google-service-account-setup.md
  - docs/plans/**
  - docs/specs/**
  - docs/superpowers/**
```

**Editorial tree walk semantics:** when an entry is a directory, the walk is
recursive over `.md` files. When an entry is a single file path, only that
file is processed. `ignore` patterns are applied to both — a file matched by
`ignore` is silently excluded from the entry list.

### Inline marker syntax

Three marker types, all written as HTML comments (invisible in GitHub's
rendered Markdown view):

**Top-of-doc trigger declaration**

```markdown
<!-- freshness:triggers
  src/Humans.Application/Services/Teams/**
  src/Humans.Domain/Entities/Team*.cs
  src/Humans.Domain/Constants/SystemTeamIds.cs
-->
```

When any glob fires, the doc is dirty. Without sub-block markers, the entire
doc gets flagged for review (no auto-update). With `freshness:auto` blocks,
those blocks regenerate and the rest is flagged.

**Auto-update sub-block**

```markdown
<!-- freshness:auto id="system-team-ids" prompt="Regenerate from src/Humans.Domain/Constants/SystemTeamIds.cs" -->
| Team | GUID | Purpose |
| ---- | ---- | ------- |
| Volunteers | 00000000-0000-0001-0000-000000000001 | All approved volunteers |
| ...
<!-- /freshness:auto -->
```

The skill regenerates content between the markers using `prompt`. `id` must be
unique within the doc. For prompts longer than a one-liner, use
`prompt-file="path/to/prompt.md"` to reference an external file relative to
repo root.

**Flag-on-change annotation**

```markdown
<!-- freshness:flag-on-change
  Authorization rules — review when controllers/services in this section change.
-->

## Authorization
```

Annotates a section that should be flagged when triggers fire, with a hint as
to *why*. The hint surfaces in the report verbatim. Pure flag — no
auto-update.

### Marker rules and validation

- `freshness:triggers` must be at the top of the doc (before the `# H1`).
- Every `freshness:auto` open tag must have a matching `<!-- /freshness:auto -->` close tag.
- `id` attributes must be unique within a doc.
- `prompt` and `prompt-file` are mutually exclusive.
- Triggers must be valid glob patterns; the validator warns on patterns that
  match zero files at validation time (likely a typo or rename).

## The skill

### Location

`.claude/skills/freshness-sweep/SKILL.md`. Project-specific, lives in the repo
so anyone working on it (or any agent dispatched into it) discovers the
skill. Frontmatter follows the existing project skill pattern (see
`.claude/skills/nav-audit/SKILL.md`).

### Invocation

```
/freshness-sweep                     # default: diff mode, batch
/freshness-sweep --full              # full-scan; ignores anchor; weekly cadence
/freshness-sweep --interactive       # stops at every question instead of accumulating
/freshness-sweep --since <ref>       # override anchor (debugging, recovery)
/freshness-sweep --scope <pattern>   # only run entries whose id matches glob
```

### Phases (canonical reference)

| Phase | What happens | Failure → |
|---|---|---|
| 1 — Baseline | `git fetch upstream main`; resolve last anchor from upstream/main commit log | Error if no upstream remote; warn + full-scan if no prior sweep commit |
| 2 — Worktree | Create `.worktrees/freshness-sweep-<timestamp>` from origin/main | Error if worktree path collides |
| 3 — Discover | Parse `freshness-catalog.yml`; walk editorial trees; build entry list | Error on schema/marker violations |
| 4 — Match | Compute diff; match changed paths against entry triggers | Empty dirty list → exit clean, tear down worktree |
| 5 — Dispatch | Subagents process dirty entries (≤3 concurrent) | Per-entry failures collected, do not abort other entries |
| 6 — Aggregate | Collect changes + flags + questions; write `docs/freshness/last-report.md` | If no files changed, skip commit/PR/push |
| 7 — Commit/Push/PR | One commit, push branch, `gh pr create` | Error surfaces; worktree retained for debugging |
| 8 — Tear down | `git worktree remove` after PR (or after clean exit with no updates) | Cleanup uses `git worktree remove [--force]`, never `rm -rf` |

### Subagent contract

Each dispatched subagent receives:

- The entry record (id, target path, update method, prompt or script)
- The list of source files that triggered the entry (so the agent can read them)
- A note: "you are inside a worktree at `<path>`; commit nothing yourself"

Each subagent returns:

```json
{
  "id": "<entry id>",
  "updated": true | false,
  "files_changed": ["docs/sections/Teams.md"],
  "flags": [{"section": "Authorization", "reason": "RoleChecks signature changed"}],
  "questions": ["Should the SystemTeamIds table also include the new TestTeam?"]
}
```

Mechanical script-driven entries do not use subagents; the skill runs the
script directly and inspects `git status` to detect file changes.

### Report format

`docs/freshness/last-report.md`, regenerated each run, single file (overwritten),
committed alongside the updates so it renders in the PR. Sections:

```markdown
# Freshness Sweep Report — 2026-04-25

**Anchor:** upstream/main @ <sha>
**Mode:** diff
**Entries dirty:** 7
**Entries updated:** 5
**Entries flagged:** 2
**Questions accumulated:** 3

## Updated automatically
- `about-page-packages` — 3 packages bumped
- `dev-stats` — script regen, +2 days of stats
- ...

## Flagged for human review
### docs/sections/Teams.md
**Triggers fired:** src/Humans.Application/Services/Teams/TeamRoleService.cs
**Why:** flag-on-change annotation: "Authorization rules — review when controllers/services in this section change."
**Suggested follow-up:** review the Authorization section against current `TeamRoleService.cs` behavior.

### docs/guide/Profiles.md
**Triggers fired:** src/Humans.Web/Controllers/ProfileController.cs
**Why:** unmarked editorial doc with broad triggers — review for screenshot/instruction drift.

## Questions
- (about-page-packages) Package "Foo.Bar" has a new license MIT-1.0 not in our license table — add or skip?
- (data-model-index) Entity `BudgetGroup` was renamed to `BudgetCategoryGroup` — confirm rename in entity index?

## Skipped (errors)
- (none)
```

## Initial inventory (definitive list at v1)

### Mechanical entries

| Id | Target | Triggers (summary) | Method |
|---|---|---|---|
| `about-page-packages` | `src/Humans.Web/Views/About/Index.cshtml` (package list section) | csproj, Directory.Packages.props | prompt |
| `dev-stats` | `docs/development-stats.md` | src C#/Razor changes | script: `docs/scripts/generate-stats.sh` |
| `reforge-history` | `docs/reforge-history.csv` | src C#/Razor changes (one row per commit) | script: TBD — verify regeneration mechanism |
| `authorization-inventory` | `docs/authorization-inventory.md` | controllers, application services, authorization handlers | prompt |
| `controller-architecture-audit` | `docs/controller-architecture-audit.md` | controllers | prompt |
| `dependency-graph` | `docs/architecture/dependency-graph.md` | application services, DI registration | prompt |
| `service-data-access-map` | `docs/architecture/service-data-access-map.md` | application services, infrastructure repositories | prompt |
| `data-model-index` | `docs/architecture/data-model.md` (entity index sub-block) | domain entities, EF configurations | prompt with marker |
| `guid-reservations` | `docs/guid-reservations.md` | EF configurations, domain constants | prompt |
| `code-analysis-suppressions` | `docs/architecture/code-analysis.md` (suppressions sub-block) | `Directory.Build.props`, `tests/Directory.Build.props`, `tests/BannedSymbols.txt` | prompt with marker |
| `docs-readme-index` | `docs/README.md` | `docs/features/**`, `docs/sections/**`, `docs/guide/**` | prompt |

### Editorial trees

| Tree | Approx files | Bootstrap status |
|---|---|---|
| `docs/sections/` | 19 + 1 template (ignored) | Markers retrofitted in bootstrap pass; `Teams.md` SystemTeamIds → `freshness:auto`; data-model field tables → `freshness:auto` per section |
| `docs/features/` | ~40 | Top-of-doc `freshness:triggers` only; no sub-block markers at v1 (specs stay narrative) |
| `docs/guide/` | ~17 | Top-of-doc `freshness:triggers` pointing at the controller/view backing each guide page |
| `docs/architecture/coding-rules.md` | 1 | `freshness:triggers` on `Directory.Build.props`, analyzer config |
| `docs/architecture/design-rules.md` | 1 | `freshness:triggers` on `src/Humans.Application/**`, `src/Humans.Domain/**` |
| `docs/architecture/code-review-rules.md` | 1 | `freshness:flag-on-change` only |
| `docs/architecture/conventions.md` | 1 | `freshness:flag-on-change` only |
| `docs/seed-data.md` | 1 | `freshness:triggers` on EF configurations + migration SQL seeders |

### Ignored

| Path | Reason |
|---|---|
| `docs/architecture/maintenance-log.md` | Hand-edited cadence log |
| `docs/architecture/tech-debt-*.md` | Date-stamped plan, frozen at write time |
| `docs/architecture/screenshot-maintenance.md` | Process doc |
| `docs/sections/SECTION-TEMPLATE.md` | Template |
| `docs/admin-role-setup.md`, `docs/google-service-account-setup.md` | Operational setup |
| `docs/plans/**`, `docs/specs/**`, `docs/superpowers/**` | Date-stamped historical artifacts |
| `LICENSE`, `README.md` (repo root) | Hand-edited |

## Bootstrap plan

After the skill itself ships, the marker retrofit is the next job. Approach:

1. Subagent fan-out (≤3 concurrent), one agent per ~10 docs.
2. Each agent reads its assigned doc + likely-related source files (inferred
   from doc content, links, mentioned types).
3. Agent proposes triggers and any obvious sub-block markers.
4. Output collected as a unified PR for Peter to review and adjust.
5. After PR merge, first real `/freshness-sweep --full` run validates triggers
   actually match real paths.

Estimated: one Claude session (~1–2 hours of Peter's attention spread across
batches of agent output). One-time cost.

## Open questions (resolved)

1. **`docs/README.md` as a catalog entry.** Resolved: **include**. Listed as
   `docs-readme-index` in the mechanical inventory.

2. **`reforge-history.csv` regeneration.** Resolved: **rebuild the regeneration
   script as part of v1.** The CSV was previously produced via the `reforge`
   tool (an existing skill — see `.claude/skills/reforge/`), but no script in
   `docs/scripts/` currently invokes it. Implementation must include an
   investigation step that reconstructs how the tool was driven and persists
   the invocation as `docs/scripts/generate-reforge-history.sh` (or similar)
   so the catalog entry can refer to it. Until the script exists, the entry
   should be flagged as `update: script` with a `tbd-script` placeholder
   and the implementation plan must close the gap before v1 ships.

## Future work (not v1)

- Cron / scheduled background runs (daily diff, weekly full).
- Auto-merging if CI is green and report has zero flags.
- Sub-block markers in `docs/features/`. v1 leaves them flag-only. If certain
  feature specs have stable objective sub-blocks (e.g., acceptance-criteria
  checklists), graduating those into `freshness:auto` blocks could be a v2
  task — depends on whether they actually drift in practice.
- Cross-tree consistency checks (e.g., warn when an entity is documented in
  two section docs, or when a feature spec references a section that doesn't
  exist).
- `--dry-run` mode that prints the planned actions without writing or
  committing anything (useful for validating catalog changes).

