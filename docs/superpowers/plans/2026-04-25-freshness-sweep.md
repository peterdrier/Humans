# Freshness Sweep Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a project-specific Claude skill (`/freshness-sweep`) and supporting catalog so every drift-prone file in this repo stays in sync with the code it describes.

**Architecture:** A YAML catalog (`docs/architecture/freshness-catalog.yml`) declares mechanical entries plus a list of editorial doc trees. Editorial docs use HTML-comment markers (`freshness:triggers`, `freshness:auto`, `freshness:flag-on-change`) to declare per-doc trigger paths and bracket auto-update sub-blocks. The skill (`.claude/skills/freshness-sweep/SKILL.md`) anchors on `upstream/main` hashes (recorded in commit messages of prior sweep commits), runs in an isolated worktree branched from `origin/main`, dispatches updates to ≤3 concurrent subagents, and opens one PR per sweep with the report file (`docs/freshness/last-report.md`) committed alongside changes so it renders on GitHub.

**Tech Stack:**
- Markdown (skill, catalog, docs)
- YAML (catalog schema)
- Bash + Git Bash on Windows (skill runtime; commands assume forward slashes and `2>/dev/null` not `>NUL`)
- `gh` CLI for PR creation
- Claude Code subagents for prompt-driven entries (≤3 concurrent per global cap)
- Existing project skills referenced: `reforge` (for `reforge-history.csv` regeneration)

**Spec:** `docs/superpowers/specs/2026-04-25-freshness-sweep-design.md`

---

## File structure (created by this plan)

```
.claude/
└── skills/
    └── freshness-sweep/
        └── SKILL.md                              # The skill itself

docs/
├── architecture/
│   └── freshness-catalog.yml                     # Catalog of mechanical entries + editorial tree list
├── freshness/
│   ├── .gitkeep                                  # Ensures empty dir is committed
│   └── last-report.md                            # Report (overwritten per run)
├── scripts/
│   └── generate-reforge-history.sh               # Reconstructed reforge invocation
└── superpowers/
    └── plans/
        └── 2026-04-25-freshness-sweep.md         # This plan
```

Plus inline `freshness:auto` markers added to:
- `docs/architecture/data-model.md` (entity index sub-block)
- `docs/architecture/code-analysis.md` (suppressions sub-block)

Plus `.gitignore` updated to exclude `.worktrees/freshness-sweep-*` (worktrees are ephemeral; the report file inside the worktree is included in the sweep commit, not gitignored at repo level).

---

## Task 1: Bootstrap directories, gitignore, and empty placeholders

**Files:**
- Create: `docs/freshness/.gitkeep` (placeholder so empty dir commits)
- Create: `docs/freshness/last-report.md` (initial empty report)
- Create: `docs/architecture/freshness-catalog.yml` (skeleton)
- Create: `.claude/skills/freshness-sweep/SKILL.md` (skeleton)
- Modify: `.gitignore` (add `.worktrees/` if not already excluded)

- [ ] **Step 1: Verify current `.gitignore` state**

```bash
grep -n "worktrees" .gitignore || echo "no worktrees rule found"
```

If output shows `no worktrees rule found`, proceed to step 2. If a rule exists, skip step 2.

- [ ] **Step 2: Add `.worktrees/` to `.gitignore`**

Append to `.gitignore`:

```
# Worktrees created by /freshness-sweep and other skills
.worktrees/
```

- [ ] **Step 3: Create `docs/freshness/` directory with placeholder**

```bash
mkdir -p docs/freshness
```

Write `docs/freshness/.gitkeep`:

```
# Keeps docs/freshness/ in version control even when last-report.md is being recreated.
```

- [ ] **Step 4: Create initial empty report**

Write `docs/freshness/last-report.md`:

```markdown
# Freshness Sweep Report

No sweep has run yet. The skill at `.claude/skills/freshness-sweep/SKILL.md` will overwrite this file on its first run.
```

- [ ] **Step 5: Create catalog skeleton**

Write `docs/architecture/freshness-catalog.yml`:

```yaml
# Freshness catalog — see docs/superpowers/specs/2026-04-25-freshness-sweep-design.md
# Consumed by /freshness-sweep (.claude/skills/freshness-sweep/SKILL.md)

version: 1

# Mechanical entries — fully auto-derived. Each entry declares trigger globs
# (matched against `git diff --name-only <anchor>..upstream/main`) and an
# update method (script or prompt).
mechanical: []

# Editorial doc trees — walked recursively for .md files. Each .md may include
# inline freshness markers (freshness:triggers, freshness:auto, freshness:flag-on-change).
editorial_trees: []

# Files to exclude from both mechanical and editorial walks.
ignore: []
```

- [ ] **Step 6: Create skill skeleton**

Write `.claude/skills/freshness-sweep/SKILL.md`:

```markdown
---
name: freshness-sweep
description: "Refresh drift-prone documentation against current code. Reads docs/architecture/freshness-catalog.yml, computes diff against the last sweep's upstream/main anchor, regenerates mechanical entries in place, processes editorial markers, and opens one PR per sweep with a report file committed alongside changes."
argument-hint: "[--full] [--interactive] [--since <ref>] [--scope <pattern>]"
---

# Freshness Sweep

Skeleton — full content authored in Task 2.
```

- [ ] **Step 7: Verify everything created and parses**

```bash
ls docs/freshness/ docs/architecture/freshness-catalog.yml .claude/skills/freshness-sweep/SKILL.md
```

Expected: all three exist, no errors.

- [ ] **Step 8: Commit**

```bash
git add .gitignore docs/freshness/ docs/architecture/freshness-catalog.yml .claude/skills/freshness-sweep/SKILL.md
git commit -m "$(cat <<'EOF'
chore: scaffold freshness-sweep skill structure

Empty catalog, skeleton SKILL.md, and docs/freshness/ placeholder report.
Real content in subsequent tasks.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Author the full skill body

**Files:**
- Modify: `.claude/skills/freshness-sweep/SKILL.md` (replace skeleton with full instructions)

The skill is markdown instructions Claude executes. There is no separate runtime — Claude is the runtime, reading the skill and using `Bash`, `Read`, `Edit`, `Write`, `Grep`, `Glob`, and `Agent` tools to carry out each phase.

- [ ] **Step 1: Replace the SKILL.md skeleton with the full content**

Write `.claude/skills/freshness-sweep/SKILL.md`:

````markdown
---
name: freshness-sweep
description: "Refresh drift-prone documentation against current code. Reads docs/architecture/freshness-catalog.yml, computes diff against the last sweep's upstream/main anchor, regenerates mechanical entries in place, processes editorial markers, and opens one PR per sweep with a report file committed alongside changes."
argument-hint: "[--full] [--interactive] [--since <ref>] [--scope <pattern>]"
---

# Freshness Sweep

Refresh drift-prone documentation files against current code. See
`docs/superpowers/specs/2026-04-25-freshness-sweep-design.md` for full design
rationale.

## Invocation

```
/freshness-sweep                     # default: diff mode, batch
/freshness-sweep --full              # weekly full-scan
/freshness-sweep --interactive       # stop at every question
/freshness-sweep --since <ref>       # override anchor for debugging
/freshness-sweep --scope <pattern>   # only run entries whose id matches glob
```

## Mode flags

- `--full`: skip anchor resolution and diff matching; every catalog entry is dirty.
- `--interactive`: stop and ask Peter at each question; default is batch (questions accumulate in the report).
- `--since <ref>`: override the auto-resolved anchor with an explicit git ref. Useful for re-processing a known range.
- `--scope <glob>`: only process entries whose `id` matches the glob (e.g., `--scope 'about-*'`).

## Phase 1: Resolve baseline

1. Run `git fetch upstream main` (always, regardless of mode). If `upstream` remote is missing, error out with: "freshness-sweep requires an `upstream` remote pointing to nobodies-collective/Humans. Configure with: `git remote add upstream https://github.com/nobodies-collective/Humans.git`".
2. If `--full` mode: skip steps 3-4. The new anchor for the commit message will be `upstream/main` HEAD; jump to Phase 2.
3. Resolve last anchor: `git log upstream/main --grep='freshness sweep' --format=%H -n 1`. Then read its commit message: `git log -1 <hash> --format=%B`. Extract the `(upstream@<sha>)` token from the message — that is the **previous anchor**.
4. If no prior sweep commit found on upstream/main: warn ("No prior freshness sweep on upstream/main — falling back to full-scan") and behave as `--full`.

The new anchor — recorded in this run's commit message — is always `upstream/main` HEAD as of the fetch in step 1.

## Phase 2: Create worktree

1. Generate timestamp: `TS=$(date -u +%Y-%m-%dT%H%M%SZ)`.
2. Run `git worktree add .worktrees/freshness-sweep-$TS -b freshness-sweep/$TS origin/main`.
3. `cd .worktrees/freshness-sweep-$TS`. All subsequent commands run inside the worktree.

If the worktree path already exists or the branch name collides, error out and instruct the user to clean up old worktrees with `git worktree list` and `git worktree remove`.

## Phase 3: Discover entries

1. Read `docs/architecture/freshness-catalog.yml` with the `Read` tool.
2. Validate schema:
   - `version` must equal `1`.
   - `mechanical`, `editorial_trees`, `ignore` must each be lists.
   - Every mechanical entry must have `id`, `target`, `triggers` (list), and either `update: script` with `script` field OR `update: prompt` with `prompt` (or `prompt-file`) field.
   - All `id` values must be unique within `mechanical`.
3. Walk `editorial_trees`. For each entry:
   - If it ends in `/`, recursively glob `**/*.md` under that path.
   - If it is a single file path, include just that file.
   - Filter out paths matching any `ignore` glob.
4. For each editorial `.md`, read the file and parse inline markers:
   - `<!-- freshness:triggers ...path patterns... -->` (one per doc, before the `# H1`)
   - `<!-- freshness:auto id="..." prompt="..." -->` … `<!-- /freshness:auto -->` (any number, must have closing tag)
   - `<!-- freshness:flag-on-change \n  reason \n -->` (any number)

   Validation:
   - Each `freshness:auto` must have a closing tag.
   - `id` attributes within a single doc must be unique.
   - `prompt` and `prompt-file` are mutually exclusive.
5. Build the unified entry list: every mechanical entry plus every editorial doc that has triggers (explicit or via flag-on-change) is a candidate. Editorial docs in `editorial_trees` with no markers at all are still candidates but treated as "unmarked editorial — flag on any src/ change".

If validation fails at any point, abort the run and print the specific error. Do not create commits or PRs.

## Phase 4: Match dirty entries

1. If `--full` mode or fallback: skip this phase. Every candidate is dirty.
2. Otherwise, get the diff path list: `git diff --name-only <previous-anchor>..upstream/main`.
3. For each candidate entry, glob-match the changed paths against its triggers:
   - Mechanical entry: triggers from `triggers` field.
   - Editorial entry with `freshness:triggers`: those globs.
   - Editorial entry without `freshness:triggers` (unmarked): trigger is `src/**` (broad catch-all, with a warning emitted to encourage the doc owner to add proper markers).
4. The dirty list is every candidate where at least one trigger matched at least one changed path.

If `--scope <glob>` was passed, filter dirty list to entries whose `id` matches.

If dirty list is empty: print "No entries dirty — nothing to refresh." and proceed to Phase 8 (cleanup).

## Phase 5: Dispatch updates

Run dispatched updates in batches of ≤3 concurrent subagents (the global hard cap from `~/.claude-shared/shared/claude.md`). Within each batch:

1. **Mechanical script-driven entries**: do NOT dispatch a subagent. Run the script directly via `Bash`. After the script completes, run `git status --short` to discover changed files. If the script touches files outside `target`, log a warning but accept all changes.

2. **Mechanical prompt-driven entries**: dispatch one subagent per entry. The subagent prompt template is:

   ```
   You are inside a worktree at <worktree-path>. Do not commit anything yourself; the parent skill handles commits.

   Your task: regenerate the file <target> per the prompt below. The trigger paths that fired are:
   <list of changed source files>

   <prompt content from the catalog entry>

   When done, return a single JSON object with this shape:
   {
     "id": "<entry id>",
     "updated": true | false,
     "files_changed": ["<paths edited>"],
     "flags": [],
     "questions": []
   }
   ```

3. **Editorial entries with `freshness:auto` blocks**: dispatch one subagent. The subagent's task is to regenerate every `freshness:auto` block in the doc per its inline `prompt`/`prompt-file`, leaving everything outside the markers untouched. Same JSON return shape.

4. **Editorial entries with `freshness:flag-on-change`** (no auto blocks): no subagent needed. The skill itself adds an entry to the flag list with the reason text from the marker.

5. **Unmarked editorial entries**: no subagent. Add an entry to the flag list with reason "Unmarked editorial doc; review for drift against changed source files: <list>".

After each batch completes, the skill reads each subagent's returned JSON, accumulates `files_changed`, `flags`, and `questions` into the run's aggregate, and proceeds to the next batch.

If `--interactive` mode and any `questions` are non-empty after a batch: stop, ask Peter inline, incorporate the answer (which may mean re-dispatching the entry), continue.

## Phase 6: Aggregate and write report

1. Compose `docs/freshness/last-report.md` with this structure:

   ```markdown
   # Freshness Sweep Report — YYYY-MM-DD HH:MM:SS UTC

   **Anchor:** upstream/main @ <sha> (previous: <prev-sha>)
   **Mode:** diff | full
   **Entries dirty:** N
   **Entries updated:** N
   **Entries flagged:** N
   **Questions accumulated:** N

   ## Updated automatically

   - `<entry id>` — <one-line summary>
   - ...

   ## Flagged for human review

   ### <file path>
   **Triggers fired:** <list of changed source files>
   **Why:** <reason from flag-on-change marker, or "unmarked editorial">
   **Suggested follow-up:** <if subagent provided one, else blank>

   ## Questions

   - (`<entry id>`) <question text>

   ## Skipped (errors)

   - (`<entry id>`) <error text>
   ```

2. Write to `docs/freshness/last-report.md` (overwrite).

3. If no `files_changed` from any subagent AND no script touched any files: do not commit, do not push, do not open a PR. Print "Nothing to update — exiting clean." Skip to Phase 8.

4. Otherwise: stage all changed files plus `docs/freshness/last-report.md`.

## Phase 7: Commit, push, open PR

1. Build the commit message:

   ```
   docs: freshness sweep — N entries (upstream@<new-anchor-sha>)

   Updated:
   - <entry id 1>
   - <entry id 2>
   - ...

   Flagged for review (see docs/freshness/last-report.md):
   - <file 1>
   - <file 2>
   - ...

   Mode: diff | full
   Previous anchor: <prev-sha>
   ```

   The `(upstream@<sha>)` token in the title MUST be present and parseable — the next sweep relies on it to resolve the prior anchor.

2. Commit: `git commit -m "$(cat <<'EOF'\n<message>\nEOF\n)"`.

3. Push: `git push -u origin freshness-sweep/$TS`.

4. Open PR:

   ```bash
   gh pr create --repo peterdrier/Humans --base main \
     --title "docs: freshness sweep — N entries (upstream@<sha>)" \
     --body "$(cat <<'EOF'
   ## Summary
   <bullets summarizing report>

   ## Report
   See `docs/freshness/last-report.md` (committed in this PR).

   ## Test plan
   - [ ] Skim the diff
   - [ ] Read the report
   - [ ] Verify flagged items in their original docs
   - [ ] Merge if happy

   🤖 Generated with [Claude Code](https://claude.com/claude-code)
   EOF
   )"
   ```

5. Print the PR URL.

## Phase 8: Tear down worktree

1. `cd ..` back to the main checkout's parent (or anywhere outside the worktree).
2. Run `git worktree remove .worktrees/freshness-sweep-$TS`. If it fails because of uncommitted state (shouldn't happen if Phase 7 succeeded, but possible if Phase 7 errored): use `git worktree remove --force` (per Peter's memory: never `rm -rf` for worktree cleanup).
3. The branch `freshness-sweep/$TS` stays on origin until the PR is merged or closed (GitHub handles branch lifecycle).

## Failure modes

| Mode | Behavior |
|---|---|
| No `upstream` remote | Error in Phase 1; nothing else runs |
| Catalog YAML parse error | Error in Phase 3; nothing else runs |
| Marker validation error in some doc | That doc is added to "Skipped (errors)" in the report; other entries continue |
| Subagent fails (returns malformed JSON or errors) | That entry is skipped; other entries continue; failure recorded in "Skipped (errors)" |
| Push fails (network, auth) | Worktree retained; user fixes credentials and re-runs Phase 7 manually |
| `gh pr create` fails | Worktree retained; report and commit are on the branch; user opens PR manually |

## What this skill does NOT do

- Schedule itself. Cadence is manual (likely daily for diff, weekly for full).
- Merge PRs. CI runs and the merge decision are Peter's.
- Touch files outside the catalog or editorial trees.
- Run if there are uncommitted changes anywhere outside the worktree (worktree is isolated; main checkout is irrelevant).
- Update `docs/architecture/maintenance-log.md` (hand-maintained for non-automated cadences).
````

- [ ] **Step 2: Validate the SKILL.md content is well-formed markdown**

```bash
head -5 .claude/skills/freshness-sweep/SKILL.md
```

Expected: yaml frontmatter (`---`, `name:`, `description:`, `argument-hint:`, `---`) followed by the H1.

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/freshness-sweep/SKILL.md
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): author full skill body

Phases 1-8 with worktree creation, anchor resolution from upstream/main
commit-message tokens, subagent dispatch, PR creation, and teardown.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Add the dev-stats mechanical entry (script-driven path)

`dev-stats` is the simplest mechanical entry — it has an existing script (`docs/scripts/generate-stats.sh`) and validates the script-driven code path.

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Inspect the existing script to confirm it works as-is**

```bash
head -20 docs/scripts/generate-stats.sh
```

Expected: bash script with usage comment `cd <repo-root> && bash docs/scripts/generate-stats.sh`. Confirm it writes to `docs/development-stats.md`.

- [ ] **Step 2: Add the entry to the catalog**

Edit `docs/architecture/freshness-catalog.yml` — replace the `mechanical: []` line with:

```yaml
mechanical:
  - id: dev-stats
    target: docs/development-stats.md
    triggers:
      - "src/**/*.cs"
      - "src/**/*.cshtml"
      - "src/**/*.razor"
    update: script
    script: docs/scripts/generate-stats.sh
```

- [ ] **Step 3: Smoke-test the entry by simulating a sweep**

Manually run the script the way the skill would:

```bash
bash docs/scripts/generate-stats.sh
git status --short docs/development-stats.md
```

Expected: either no diff (already current) or a small diff in `docs/development-stats.md`. If the script errors out, fix the entry's `script` path or the script itself before continuing.

- [ ] **Step 4: Restore the file** (we don't want this in the design PR)

```bash
git checkout -- docs/development-stats.md
```

- [ ] **Step 5: Commit**

```bash
git add docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add dev-stats catalog entry

First mechanical entry, script-driven. Validates the script path of the
catalog schema using the existing docs/scripts/generate-stats.sh.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Add the about-page-packages entry (prompt-driven path)

Validates the prompt-driven code path. The About page lists every production NuGet package and frontend CDN dependency with versions and licenses; it goes stale after every NuGet update.

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Inspect the About page and Directory.Packages.props**

```bash
grep -n "PackageReference\|Version\|<Package" src/Humans.Web/Views/About/Index.cshtml | head -20
cat Directory.Packages.props | head -40
```

Note the structure: About page has package cards/sections; `Directory.Packages.props` is the central version source. The skill's prompt must produce updates that match the existing layout.

- [ ] **Step 2: Append the entry to the catalog**

Edit `docs/architecture/freshness-catalog.yml` — append under `mechanical:`:

```yaml
  - id: about-page-packages
    target: src/Humans.Web/Views/About/Index.cshtml
    triggers:
      - "Directory.Packages.props"
      - "**/*.csproj"
    update: prompt
    prompt: |
      Read Directory.Packages.props and every .csproj file in the repo.
      Build a list of production NuGet packages (exclude test-only packages
      that are referenced only from anything under tests/).

      Open src/Humans.Web/Views/About/Index.cshtml. Find the package list
      section ("The Ingredients" and any package cards below it). Update
      version numbers in place. Add new packages following the existing
      card structure with a one-line description (carry over any existing
      description for packages already in the file). Remove cards for
      packages no longer referenced.

      Preserve all prose, contributor names, the license section, and any
      hand-written copy outside the package cards. Do not invent licenses
      or descriptions; if you don't know a new package's license, leave a
      `<!-- TODO: license -->` HTML comment next to its card and add a
      question to your return JSON asking Peter to confirm.
```

- [ ] **Step 3: Validate the catalog still parses**

```bash
cat docs/architecture/freshness-catalog.yml | head -40
```

Expected: well-formed YAML with two mechanical entries (`dev-stats` and `about-page-packages`).

- [ ] **Step 4: Commit**

```bash
git add docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add about-page-packages catalog entry

First prompt-driven entry. Triggers on csproj or Directory.Packages.props
changes; subagent regenerates the package list in About/Index.cshtml.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Add the docs-readme-index entry

`docs/README.md` indexes `docs/features/`, `docs/sections/`, and `docs/guide/`. It drifts when files are added/removed/renamed.

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Inspect the current `docs/README.md` structure**

```bash
head -20 docs/README.md
```

Note the table format: feature name → relative link → one-line description.

- [ ] **Step 2: Append the entry**

Edit `docs/architecture/freshness-catalog.yml` — append under `mechanical:`:

```yaml
  - id: docs-readme-index
    target: docs/README.md
    triggers:
      - "docs/features/**/*.md"
      - "docs/sections/**/*.md"
      - "docs/guide/**/*.md"
    update: prompt
    prompt: |
      Walk docs/features/, docs/sections/, and docs/guide/. For each .md file
      (excluding SECTION-TEMPLATE.md, README.md, GettingStarted.md, Glossary.md),
      derive a one-line description from the first paragraph of the file (or
      the H1 + first sentence if the first paragraph is too long).

      Update docs/README.md to list every doc found, grouped by directory,
      using the existing table layout (file name → relative link → description).
      Preserve any hand-written prose at the top of docs/README.md (intro,
      pointers to architecture/, etc.). Only regenerate the indexed tables.
```

- [ ] **Step 3: Commit**

```bash
git add docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add docs-readme-index catalog entry

Regenerates the indexed tables in docs/README.md when files in
features/, sections/, or guide/ are added/removed/renamed.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Add the authorization-inventory entry

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Inspect the current inventory format**

```bash
head -30 docs/authorization-inventory.md
```

Note the table format: Controller | Scope | Roles | Source.

- [ ] **Step 2: Append the entry**

```yaml
  - id: authorization-inventory
    target: docs/authorization-inventory.md
    triggers:
      - "src/Humans.Web/Controllers/**/*.cs"
      - "src/Humans.Application/**/*.cs"
      - "src/Humans.Web/Authorization/**/*.cs"
      - "src/Humans.Domain/Authorization/**/*.cs"
    update: prompt
    prompt: |
      Regenerate docs/authorization-inventory.md from scratch.

      Find every:
      - [Authorize(Roles = ...)] / [Authorize(Policy = ...)] attribute on
        controllers and actions
      - RoleChecks.* / ShiftRoleChecks.* invocation
      - IAuthorizationService.AuthorizeAsync call site
      - Resource-based authorization handler (subclass of AuthorizationHandler<T, R>)

      Group by section (Admin, Tickets, Teams, Profiles, Camps, etc. — match
      the existing section headings).

      Match the existing column layout: Controller | Scope | Roles | Source.
      "Scope" is "Class" for class-level [Authorize], "Action" for
      action-level. "Roles" is the comma-separated role list. "Source" names
      the static field (e.g. RoleNames.Admin, RoleGroups.BoardOrAdmin) when
      identifiable.

      Preserve the file's existing intro paragraph and any non-table prose
      at the top.
```

- [ ] **Step 3: Commit**

```bash
git add docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add authorization-inventory catalog entry

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Add the controller-architecture-audit entry

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Inspect**

```bash
head -30 docs/controller-architecture-audit.md
```

- [ ] **Step 2: Append the entry**

```yaml
  - id: controller-architecture-audit
    target: docs/controller-architecture-audit.md
    triggers:
      - "src/Humans.Web/Controllers/**/*.cs"
    update: prompt
    prompt: |
      Regenerate the action-name audit in docs/controller-architecture-audit.md.

      For every controller in src/Humans.Web/Controllers/:
      1. List every public action method with its [HttpGet]/[HttpPost]/etc.
         verb and route.
      2. For each action, derive a Purpose line from the action name and
         body comments.
      3. Suggest a rename only if the action name violates the project's
         action-name conventions documented in
         docs/architecture/conventions.md (or note "OK" if it doesn't).

      Match the existing column layout: Method | Route | Verb | Purpose | Suggestion.
      Preserve the document header (the "Living document. Last updated: ..." line)
      and update the date.
```

- [ ] **Step 3: Commit**

```bash
git add docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add controller-architecture-audit catalog entry

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Add the dependency-graph entry

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Inspect the existing graph**

```bash
head -50 docs/architecture/dependency-graph.md
```

Note the Mermaid diagram and the section-color classDefs.

- [ ] **Step 2: Append the entry**

```yaml
  - id: dependency-graph
    target: docs/architecture/dependency-graph.md
    triggers:
      - "src/Humans.Application/**/*.cs"
      - "src/Humans.Web/Program.cs"
      - "src/Humans.Web/ServiceCollectionExtensions/**/*.cs"
    update: prompt
    prompt: |
      Regenerate the Mermaid dependency graph in docs/architecture/dependency-graph.md.

      Walk every service class under src/Humans.Application/Services/. For
      each:
      1. Read the constructor; identify every injected interface.
      2. For each injected interface, follow it to its implementing service
         to identify the section it belongs to (use the folder name under
         src/Humans.Application/Services/ as the section).
      3. Distinguish ctor-injected (solid arrow) from
         IServiceProvider.GetRequiredService<T>() (dashed arrow with "lazy"
         label).
      4. Identify cross-cutting services (AuditLogService, EmailService,
         NotificationService, RoleAssignmentService, HumansMetrics) — place
         these on the right side of the graph as a separate cluster.

      Preserve the document header ("How to read", classDef colors, and any
      explanatory prose). Only regenerate the Mermaid diagram body.
```

- [ ] **Step 3: Commit**

```bash
git add docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add dependency-graph catalog entry

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Add the service-data-access-map entry

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Inspect**

```bash
head -30 docs/architecture/service-data-access-map.md
```

- [ ] **Step 2: Append the entry**

```yaml
  - id: service-data-access-map
    target: docs/architecture/service-data-access-map.md
    triggers:
      - "src/Humans.Application/Services/**/*.cs"
      - "src/Humans.Infrastructure/Repositories/**/*.cs"
      - "src/Humans.Infrastructure/Data/HumansDbContext.cs"
    update: prompt
    prompt: |
      Regenerate docs/architecture/service-data-access-map.md.

      For every service under src/Humans.Application/Services/, identify:
      1. Which repository interfaces it depends on.
      2. Which database tables those repositories ultimately access (resolve
         via the HumansDbContext.cs DbSet<> declarations).
      3. Which IMemoryCache keys the service reads/writes.

      Group by section (use folder name under Services/). Match the existing
      section headings and table layout. Preserve the intro paragraph and
      Table of Contents.

      Note: at scale ~500 users with single-server deployment, this map is
      diagnostic — call out cross-section table reads (a service reading a
      table owned by another section's repository) as a design rule
      violation per docs/architecture/design-rules.md §"Services own their data".
```

- [ ] **Step 3: Commit**

```bash
git add docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add service-data-access-map catalog entry

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Add the data-model-index entry (with marker)

The full data-model.md is editorial (it has prose explaining cross-section rules), but the entity index table at the top is fully objective. Use a `freshness:auto` marker to bracket just the table.

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`
- Modify: `docs/architecture/data-model.md` (add markers around the entity index table)

- [ ] **Step 1: Identify the entity index table location**

```bash
grep -n "## Entity index\|^| Entity\|^|---" docs/architecture/data-model.md | head -10
```

Note the line numbers of `## Entity index` and the end of the table (the last `| <something> | <section> | ... |` row before the next `##` heading).

- [ ] **Step 2: Wrap the table with markers**

Edit `docs/architecture/data-model.md`. Insert immediately after `## Entity index` (and before the table header):

```markdown
<!-- freshness:auto id="entity-index" prompt="Walk every class under src/Humans.Domain/Entities/ that has a corresponding configuration under src/Humans.Infrastructure/Data/Configurations/. For each, identify the owning section (find the section doc in docs/sections/ whose Data Model section names the entity). Build the entity index table with columns: Entity | Owning section | Notes. Preserve any per-row Notes column content the existing table already has — only update entity names and section links if they changed." -->
```

Then, immediately AFTER the last row of the entity index table (and before the next `##` heading), insert:

```markdown
<!-- /freshness:auto -->
```

- [ ] **Step 3: Append the catalog entry**

Edit `docs/architecture/freshness-catalog.yml` — append under `mechanical:`:

```yaml
  - id: data-model-index
    target: docs/architecture/data-model.md
    triggers:
      - "src/Humans.Domain/Entities/**/*.cs"
      - "src/Humans.Infrastructure/Data/Configurations/**/*.cs"
    update: prompt
    prompt-file: ".claude/skills/freshness-sweep/prompts/data-model-index.md"
```

- [ ] **Step 4: Create the prompt file**

Create `.claude/skills/freshness-sweep/prompts/` directory:

```bash
mkdir -p .claude/skills/freshness-sweep/prompts
```

Write `.claude/skills/freshness-sweep/prompts/data-model-index.md`:

```markdown
Process the freshness:auto block with id="entity-index" in
docs/architecture/data-model.md. The inline marker prompt is the
authoritative instruction; this file exists only because the marker prompt
duplicates with the catalog entry — the skill should prefer the inline
marker.

If the inline marker is missing or malformed, fall back to: regenerate the
"## Entity index" table by walking src/Humans.Domain/Entities/ and matching
to docs/sections/ owning sections, using columns Entity | Owning section | Notes.
```

(The prompt-file is a fallback; in practice the inline marker drives the
update. Documenting this dual-path in the skill is in Task 13.)

- [ ] **Step 5: Commit**

```bash
git add docs/architecture/freshness-catalog.yml docs/architecture/data-model.md .claude/skills/freshness-sweep/prompts/
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add data-model-index catalog entry with auto marker

The data-model.md doc is editorial overall but has an objective entity
index table at the top. Wrapped in a freshness:auto block; the catalog
entry references a prompt file as fallback only — the inline marker is
authoritative.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Add the guid-reservations entry

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Inspect**

```bash
cat docs/guid-reservations.md
```

- [ ] **Step 2: Append**

```yaml
  - id: guid-reservations
    target: docs/guid-reservations.md
    triggers:
      - "src/Humans.Infrastructure/Data/Configurations/**/*.cs"
      - "src/Humans.Domain/Constants/**/*.cs"
    update: prompt
    prompt: |
      Regenerate the "Current Reservations" table in docs/guid-reservations.md.

      Walk src/Humans.Domain/Constants/ and src/Humans.Infrastructure/Data/Configurations/.
      Find every deterministic GUID literal (`new Guid("0000...")` or string
      literals in HasData/HasData<T>). Group GUIDs by their leading 4-hex
      block (e.g., `0001`, `0002`).

      For each block, derive Purpose from the file path / class name and
      Source as a relative link to that file.

      Preserve the document's prose sections ("Rules", and the explanatory
      intro). Only regenerate the table.
```

- [ ] **Step 3: Commit**

```bash
git add docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add guid-reservations catalog entry

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Add the code-analysis-suppressions entry (with marker)

`docs/architecture/code-analysis.md` lists analyzer suppressions; the suppression list is objective, the rest is prose.

**Files:**
- Modify: `docs/architecture/code-analysis.md` (add markers around the suppression list)
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Identify the suppression list**

```bash
grep -n "Common suppressions\|^- `MA\|^- `RCS" docs/architecture/code-analysis.md | head -10
```

Note the line range of the suppression list.

- [ ] **Step 2: Wrap the suppression list with markers**

Edit `docs/architecture/code-analysis.md`. Insert before the line that says `**Common suppressions in \`Directory.Build.props\`:**`:

```markdown
<!-- freshness:auto id="suppressions" prompt="Read Directory.Build.props at repo root. Find the <NoWarn> property and any per-rule <Rule Id=...> elements. List each suppression as `- \`<RULE_ID>\` - <one-line description>`. Use Roslyn analyzer documentation knowledge to fill the description (e.g., MA0048 = 'File name must match type name'). If a rule ID is unknown, write 'TBD: look up rule description'." -->
```

Then, immediately AFTER the last `- \`...\`` line of the suppression list (and before the next `##` or paragraph), insert:

```markdown
<!-- /freshness:auto -->
```

- [ ] **Step 3: Append the catalog entry**

```yaml
  - id: code-analysis-suppressions
    target: docs/architecture/code-analysis.md
    triggers:
      - "Directory.Build.props"
      - "tests/Directory.Build.props"
      - "tests/BannedSymbols.txt"
    update: prompt
    prompt: |
      Process the freshness:auto block with id="suppressions" in
      docs/architecture/code-analysis.md per its inline prompt.
```

- [ ] **Step 4: Commit**

```bash
git add docs/architecture/freshness-catalog.yml docs/architecture/code-analysis.md
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add code-analysis-suppressions catalog entry with marker

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 13: Reconstruct reforge-history regeneration script

`docs/reforge-history.csv` was previously regenerated using the `reforge` skill, but no script in `docs/scripts/` invokes it currently. This task reconstructs the invocation.

**Files:**
- Create: `docs/scripts/generate-reforge-history.sh`
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Inspect the current CSV columns**

```bash
head -1 docs/reforge-history.csv
```

Record the column list. Sample columns observed at v1: `commit_date,commit,solution,loc_prod,loc_test,files_prod,files_test,classes,interfaces,avg_reach,p95_reach,max_reach,max_reach_file,core_size_pct,core_file_count,cycle_count,avg_fanout,max_fanout,max_fanout_file,avg_cyclomatic,p95_cyclomatic,max_cyclomatic,max_cyclomatic_method,avg_class_loc,p95_class_loc,max_class_loc,max_class_loc_name`.

- [ ] **Step 2: Inspect the reforge skill to find its CLI**

```bash
cat .claude/skills/reforge/SKILL.md 2>/dev/null | head -30
```

Identify the binary or command the skill uses (likely `reforge analyze` or similar). Note the flags needed to produce the existing CSV columns.

- [ ] **Step 3: Author the regeneration script**

Write `docs/scripts/generate-reforge-history.sh`:

```bash
#!/bin/bash
# Regenerate docs/reforge-history.csv — one row per commit on main, with
# semantic codebase metrics from the reforge tool.
#
# Usage: cd <repo-root> && bash docs/scripts/generate-reforge-history.sh [--incremental]
#
# Modes:
#   default:       full regeneration (slow; iterates every commit on main)
#   --incremental: append rows for commits since the last row in the existing CSV
#
# Requirements:
#   - reforge CLI on PATH (see .claude/skills/reforge/ for setup)
#   - clean working tree (will stash if needed)
#   - Humans.slnx as the analysis solution

set -euo pipefail

CSV=docs/reforge-history.csv
SOLUTION=Humans.slnx
HEADER='commit_date,commit,solution,loc_prod,loc_test,files_prod,files_test,classes,interfaces,avg_reach,p95_reach,max_reach,max_reach_file,core_size_pct,core_file_count,cycle_count,avg_fanout,max_fanout,max_fanout_file,avg_cyclomatic,p95_cyclomatic,max_cyclomatic,max_cyclomatic_method,avg_class_loc,p95_class_loc,max_class_loc,max_class_loc_name'

INCREMENTAL=false
if [ "${1:-}" = "--incremental" ]; then
  INCREMENTAL=true
fi

# Stash dirty state
ORIG_REF=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "main")
NEEDS_STASH=false
if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
  git stash --quiet
  NEEDS_STASH=true
fi

cleanup() {
  git checkout --quiet "$ORIG_REF" || true
  if [ "$NEEDS_STASH" = "true" ]; then
    git stash pop --quiet || true
  fi
}
trap cleanup EXIT

# Determine commit range
if [ "$INCREMENTAL" = "true" ] && [ -f "$CSV" ]; then
  LAST_COMMIT=$(tail -n 1 "$CSV" | cut -d, -f2)
  COMMITS=$(git log --reverse --format=%H "$LAST_COMMIT..main")
else
  echo "$HEADER" > "$CSV"
  COMMITS=$(git log --reverse --format=%H main)
fi

for COMMIT in $COMMITS; do
  git checkout --quiet "$COMMIT"
  COMMIT_DATE=$(git log -1 --format=%cI "$COMMIT")
  SHORT=$(git rev-parse --short "$COMMIT")

  # Invoke reforge — exact flags TBD during Step 2 investigation
  ROW=$(reforge analyze --solution "$SOLUTION" --format csv-row --columns "$HEADER" 2>/dev/null) || {
    echo "reforge failed on $COMMIT — skipping"
    continue
  }

  # Prepend commit metadata
  echo "$COMMIT_DATE,$SHORT,$SOLUTION,$ROW" >> "$CSV"
done

echo "Done. $(wc -l < "$CSV") rows in $CSV."
```

NOTE: The exact `reforge analyze` invocation in the loop is the one
unknown — Step 2's investigation must produce the precise flags. If the
investigation reveals that the existing CSV was hand-stitched from raw
reforge output rather than a single command, the script body above must be
revised to emit the same per-commit row from whatever raw outputs the tool
produces.

- [ ] **Step 4: Make the script executable**

```bash
chmod +x docs/scripts/generate-reforge-history.sh
```

- [ ] **Step 5: Smoke-test in incremental mode**

```bash
bash docs/scripts/generate-reforge-history.sh --incremental
git status --short docs/reforge-history.csv
```

Expected: either no diff (already current) or a small append. If `reforge` errors, the investigation in Step 2 was incomplete — return to it before continuing.

- [ ] **Step 6: Restore the CSV**

```bash
git checkout -- docs/reforge-history.csv
```

- [ ] **Step 7: Append the catalog entry**

Edit `docs/architecture/freshness-catalog.yml`:

```yaml
  - id: reforge-history
    target: docs/reforge-history.csv
    triggers:
      - "src/**/*.cs"
      - "src/**/*.cshtml"
    update: script
    script: docs/scripts/generate-reforge-history.sh --incremental
```

- [ ] **Step 8: Commit**

```bash
git add docs/scripts/generate-reforge-history.sh docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add reforge-history catalog entry + script

Reconstructs the regeneration path that produced docs/reforge-history.csv
historically. Script defaults to incremental (append rows for commits since
the last row); a `--full` flag rebuilds from scratch.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 14: Add editorial trees and ignore list

**Files:**
- Modify: `docs/architecture/freshness-catalog.yml`

- [ ] **Step 1: Edit the catalog**

Replace `editorial_trees: []` with:

```yaml
editorial_trees:
  - docs/sections/
  - docs/features/
  - docs/guide/
  - docs/architecture/coding-rules.md
  - docs/architecture/design-rules.md
  - docs/architecture/code-review-rules.md
  - docs/architecture/conventions.md
  - docs/seed-data.md
```

Replace `ignore: []` with:

```yaml
ignore:
  - docs/sections/SECTION-TEMPLATE.md
  - docs/architecture/maintenance-log.md
  - docs/architecture/tech-debt-*.md
  - docs/architecture/screenshot-maintenance.md
  - docs/admin-role-setup.md
  - docs/google-service-account-setup.md
  - docs/plans/**
  - docs/specs/**
  - docs/superpowers/**
```

- [ ] **Step 2: Verify the catalog still parses**

```bash
cat docs/architecture/freshness-catalog.yml
```

Expected: well-formed YAML with version, mechanical (~11 entries), editorial_trees (8 paths), ignore (9 patterns).

- [ ] **Step 3: Commit**

```bash
git add docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
feat(freshness-sweep): add editorial trees and ignore list

v1 lists the editorial trees the skill should walk but does not retrofit
inline markers — that's the bootstrap pass tracked separately. Until
markers are added, all editorial docs are "unmarked editorial — flag on
any src/ change", which gives broad but useful drift signal.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 15: First full-scan smoke test

Validates the skill end-to-end against the catalog. Run from a fresh worktree to mimic real usage.

**Files:**
- None (validation only)

- [ ] **Step 1: Verify upstream remote exists**

```bash
git remote -v | grep upstream
```

Expected: `upstream` points at `nobodies-collective/Humans`. If absent, configure with:

```bash
git remote add upstream https://github.com/nobodies-collective/Humans.git
git fetch upstream
```

- [ ] **Step 2: Invoke the skill in full mode from this worktree**

From the main checkout (NOT this worktree — the skill creates its own worktree):

```bash
# In main checkout
cd /h/source/Humans
```

Then in Claude:

```
/freshness-sweep --full
```

- [ ] **Step 3: Observe the run**

Expected behavior:
1. `git fetch upstream main` succeeds.
2. Skill creates `.worktrees/freshness-sweep-<TS>` from origin/main.
3. Skill parses `docs/architecture/freshness-catalog.yml`; reports 11 mechanical entries + N editorial files in scope.
4. Mechanical entries dispatch to subagents in batches of 3 — `dev-stats` and `reforge-history` run as scripts (no subagent); the other 9 run as prompt-driven subagents.
5. Editorial docs all flag (unmarked).
6. Report compiled.
7. If any files changed: commit, push, PR opened. PR URL printed.
8. If no files changed: clean exit, no PR.

- [ ] **Step 4: Review the resulting PR**

Open the PR URL. Verify:
- Commit message has `(upstream@<sha>)` token in title.
- `docs/freshness/last-report.md` is committed and renders cleanly on github.com.
- No spurious diffs (e.g., trailing-whitespace-only changes, accidentally-modified unrelated files).

- [ ] **Step 5: Iterate**

If issues found in the run:
- Update `.claude/skills/freshness-sweep/SKILL.md` to fix.
- Update catalog entries' prompts to fix.
- Push the fixes to this branch (`freshness-sweep-design`) — the design PR holds both spec and skill until merged.
- Re-run.

When the run is clean, document the run timestamp and PR URL in the commit message of this task.

- [ ] **Step 6: Commit any skill fixes from iteration**

If iteration required edits, commit them:

```bash
git add .claude/skills/freshness-sweep/SKILL.md docs/architecture/freshness-catalog.yml
git commit -m "$(cat <<'EOF'
fix(freshness-sweep): adjustments from first full-scan smoke test

<list of specific fixes>

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

If no fixes needed: skip this step.

---

## Task 16: Update CLAUDE.md and maintenance-log

**Files:**
- Modify: `CLAUDE.md` (add freshness-sweep to the "Maintenance Log" section)
- Modify: `docs/architecture/maintenance-log.md` (add a row for the skill)

- [ ] **Step 1: Add a row to the maintenance log**

Edit `docs/architecture/maintenance-log.md`. Add a row to the table (after the existing rows):

```markdown
| Freshness sweep (diff) | <smoke-test-date> | Daily | Daily | — | `/freshness-sweep` — auto-refresh drift-prone docs against upstream/main diffs |
| Freshness sweep (full) | <smoke-test-date> | <smoke-test-date+7> | Weekly | — | `/freshness-sweep --full` — full regeneration of every catalog entry |
```

- [ ] **Step 2: Add a CLAUDE.md reference**

Edit `CLAUDE.md`. In the "Extended Docs" table, add a row:

```markdown
| **Freshness catalog** | **`docs/architecture/freshness-catalog.yml`** |
```

In the "Critical: ..." section list at the top, add (under the existing critical bullets):

```markdown
- **Doc freshness is automated.** `/freshness-sweep` regenerates drift-prone
  files against `upstream/main` diffs. Catalog at
  `docs/architecture/freshness-catalog.yml`. See spec at
  `docs/superpowers/specs/2026-04-25-freshness-sweep-design.md`.
```

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md docs/architecture/maintenance-log.md
git commit -m "$(cat <<'EOF'
docs: register freshness-sweep in CLAUDE.md and maintenance-log

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 17: Push and update the design PR

**Files:**
- None (git only)

- [ ] **Step 1: Push the branch**

```bash
git push origin freshness-sweep-design
```

- [ ] **Step 2: Update the PR description**

The design PR (#334) covered only the spec. With Tasks 1-16 complete, the same branch now also contains the v1 implementation. Update the PR title/body to reflect that:

```bash
gh pr edit 334 --repo peterdrier/Humans \
  --title "feat: freshness-sweep skill + catalog (v1)" \
  --body "$(cat <<'EOF'
## Summary

Spec + v1 implementation of `/freshness-sweep`, a project-specific Claude
Code skill that keeps drift-prone documentation in sync with the code it
describes.

**Spec:** `docs/superpowers/specs/2026-04-25-freshness-sweep-design.md`
**Plan:** `docs/superpowers/plans/2026-04-25-freshness-sweep.md`

## What's included in v1

**Skill** at `.claude/skills/freshness-sweep/SKILL.md`. Invocation:
- `/freshness-sweep` — diff mode (daily)
- `/freshness-sweep --full` — full scan (weekly)
- `--interactive`, `--since <ref>`, `--scope <glob>` modifiers

**Catalog** at `docs/architecture/freshness-catalog.yml`:
- 11 mechanical entries: about-page-packages, dev-stats, reforge-history,
  authorization-inventory, controller-architecture-audit, dependency-graph,
  service-data-access-map, data-model-index (with marker),
  guid-reservations, code-analysis-suppressions (with marker),
  docs-readme-index
- 8 editorial trees walked: sections/, features/, guide/, plus four
  architecture rule docs and seed-data.md
- 9 ignore patterns

**Inline markers** added to two docs as v1 examples:
- `docs/architecture/data-model.md` — entity index sub-block
- `docs/architecture/code-analysis.md` — analyzer suppressions sub-block

**Helper script** `docs/scripts/generate-reforge-history.sh` reconstructs
the regeneration path for `docs/reforge-history.csv` using the existing
`reforge` skill.

**Smoke test** completed: `/freshness-sweep --full` ran clean against the
v1 catalog. PR <smoke-test-pr-url>.

## What's NOT in v1 (deferred)

- **Marker retrofitting across editorial docs** (~80 files). Until markers
  are added, all editorial docs flag broadly on any `src/` change — useful
  but noisy. Bootstrap pass tracked separately.
- Cron / scheduled execution. Cadence is manual (daily diff, weekly full).
- Auto-merge. Merge stays a human decision.
- Sub-block markers in `docs/features/`.

## Test plan

- [ ] Read the spec
- [ ] Skim the catalog YAML for any wrong triggers or prompts
- [ ] Skim the SKILL.md phases
- [ ] Verify the smoke-test PR looks reasonable (link in commit history)
- [ ] Approve and merge

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 3: Verify**

```bash
gh pr view 334 --repo peterdrier/Humans --json title,state,url
```

Expected: title updated, state=OPEN, URL printed.

---

## Self-review checklist (perform after writing all tasks)

**Spec coverage check:**

| Spec section | Implementing task(s) |
|---|---|
| Lifecycle Phase 1 (anchor) | Task 2 (SKILL.md) |
| Lifecycle Phase 2 (worktree) | Task 2 |
| Lifecycle Phase 3 (discover) | Task 2 |
| Lifecycle Phase 4 (match) | Task 2 |
| Lifecycle Phase 5 (dispatch) | Task 2 |
| Lifecycle Phase 6 (aggregate) | Task 2 |
| Lifecycle Phase 7 (commit/PR) | Task 2 |
| Lifecycle Phase 8 (teardown) | Task 2 |
| Full-scan mode | Task 2, Task 15 |
| Inter-promotion behavior | Task 2 (covered in Phase 1 logic) |
| Catalog YAML structure | Task 1, populated through Task 14 |
| Inline marker syntax | Task 10, Task 12 (examples); Task 2 (parsing logic) |
| Marker validation rules | Task 2 |
| Mechanical entries (11) | Tasks 3-13 |
| Editorial trees | Task 14 |
| Ignored | Task 14 |
| Bootstrap plan | NOT in v1 — explicitly deferred (spec §"Bootstrap plan") |
| Failure modes | Task 2 (table at end of SKILL.md) |
| Smoke test | Task 15 |
| Documentation updates | Task 16 |

All v1-scope spec requirements have implementing tasks. Bootstrap (marker retrofit across ~80 docs) is explicitly deferred to a follow-up plan.

**Placeholder scan:** No "TBD", "TODO" without context, or "implement later" found, except:
- Task 13 Step 3 contains a deliberate `TBD` for the exact `reforge analyze` flags — this is acknowledged as the unknown the investigation in Step 2 must close. Acceptable because the task description names the gap and assigns the investigation to the same task.

**Type/identifier consistency:**
- Worktree path pattern is consistently `.worktrees/freshness-sweep-<timestamp>` (no `<TS>`-only variant slipping through).
- Branch name pattern is consistently `freshness-sweep/<timestamp>` (forward slash).
- Anchor token in commit messages is consistently `(upstream@<sha>)`.
- Report path is consistently `docs/freshness/last-report.md` (singular `last-report`, not `report`).
- Catalog path is consistently `docs/architecture/freshness-catalog.yml` (`.yml`, not `.yaml`).
- Skill path is consistently `.claude/skills/freshness-sweep/SKILL.md`.

No identifier inconsistencies found.

---

## Execution handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-25-freshness-sweep.md`. Two execution options:

1. **Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — Execute tasks in this session using `executing-plans`, batch execution with checkpoints.

Which approach?
