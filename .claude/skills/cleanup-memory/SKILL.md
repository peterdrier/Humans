---
name: cleanup-memory
description: "Two-phase memory hygiene for the Humans repo. Phase 1 (external): scan Claude Code's per-machine external memory (`~/.claude/projects/<slug>/memory/`) for durable project rules that should be migrated into the repo's `memory/` dir, and for entries that are already duplicated by repo atoms. Phase 2 (repo): audit in-repo `memory/`, `CLAUDE.md`, `docs/architecture/`, and `docs/sections/` for dead links, content duplication, drift between atoms and the design-rules constitution, and CLAUDE.md bullets that should be atomized. Use when external memory has accumulated since the last sweep, when CLAUDE.md feels bloated, or periodically as hygiene. Triggers: '/cleanup-memory', 'audit memory', 'memory hygiene', 'migrate external memory', 'check memory bloat'."
argument-hint: "[external] [repo] [report-only]"
---

# Cleanup Memory

Audit and clean up the project's memory system across two surfaces:

- **External** — per-machine Claude Code memory at `~/.claude/projects/<slug>/memory/` (different path on each OS — DO NOT hardcode).
- **Repo** — `memory/`, `CLAUDE.md`, `docs/architecture/design-rules.md`, `docs/architecture/code-review-rules.md`, `docs/sections/`.

The two are intentionally split: the repo `memory/` syncs across machines via git and is the source of truth for *durable project rules*; the external memory is per-machine and holds *only* about-Peter / user-pref entries and active-PR working state. Read `memory/META.md` for the full design intent before working.

## Arguments

- *(none)* — run both phases: external scan → repo scan → consolidated report → ask → execute.
- `external` — only Phase 1.
- `repo` — only Phase 2.
- `report-only` — produce the full report but do not propose or apply changes.

## Hard rules (read before doing anything)

- **Never hardcode the external memory path.** It is `<user-home>/.claude/projects/<slug>/memory/` where `<slug>` is derived from the project's absolute git-root path. The slug differs per OS and per machine. See "Discovering the external memory directory" below.
- **Never delete a file before confirming the rule survives somewhere.** A duplicate in repo `memory/` is fine; a "rule lost in translation" is not. Diff content; do not match by filename alone.
- **Always work in a worktree when proposing migrations.** `H:\source\Humans\.worktrees\cleanup-memory-<date>` (or platform-equivalent under `.worktrees/`). Never edit files in the main repo checkout.
- **Per-instance Peter approval for force-push** if the cleanup PR ever needs it. Default to additive commits.
- **Migration PRs never modify the same file twice.** If Phase 1 wants to add an atom AND Phase 2 wants to consolidate it with another atom, do the add first (PR A), let it merge, then run Phase 2 to propose the consolidation (PR B).

## Discovering the external memory directory

The slug is the project's absolute git-root path with `:`, `/`, and `\` replaced by `-`. Examples:

| Project root (per OS)           | Slug                       |
|---------------------------------|----------------------------|
| `H:\source\Humans` (Windows)    | `H--source-Humans`         |
| `/home/peter/humans` (Linux)    | `-home-peter-humans`       |
| `/Users/peter/humans` (macOS)   | `-Users-peter-humans`      |

Derive at runtime — do not hardcode. **Use glob+content match as the primary mechanism**; the derived slug is only a fast-path optimization. Why: on Windows, `git rev-parse --show-toplevel` may report a different case than the directory Claude Code actually created (e.g. cwd reports `humans` lowercase while the slug dir is `H--source-Humans`). Lookup by exact slug succeeds on Windows by accident (case-insensitive FS) but fails on Linux/macOS. Glob-then-match works everywhere.

```bash
PROJECT_ROOT="$(git rev-parse --show-toplevel)"
REPO_NAME="$(basename "$PROJECT_ROOT")"

# Primary: glob and find by content (case-safe across all OSes).
EXT_DIR=""
for d in "$HOME"/.claude/projects/*/memory; do
  [ -f "$d/MEMORY.md" ] || continue
  # Match by repo name (case-insensitive) appearing in the directory name
  # OR in MEMORY.md content. Cheap to check both.
  parent_slug="$(basename "$(dirname "$d")")"
  if echo "$parent_slug" | grep -qi "$REPO_NAME" \
     || grep -qiE "(humans|nobodies)" "$d/MEMORY.md" 2>/dev/null; then
    EXT_DIR="$d"
    break
  fi
done

# Fallback: derived slug (still useful if glob found nothing — e.g. exotic
# project name that doesn't match the substring check).
if [ -z "$EXT_DIR" ]; then
  SLUG="$(printf '%s' "$PROJECT_ROOT" | sed 's|[:/\\]|-|g')"
  candidate="$HOME/.claude/projects/$SLUG/memory"
  [ -d "$candidate" ] && EXT_DIR="$candidate"
fi
```

If `$EXT_DIR` is still empty, ask Peter — do not guess. The external dir might not exist on a fresh machine, in which case Phase 1 has nothing to do; report that and skip to Phase 2.

---

## Phase 1 — External memory scan

### Goal

Sort every file in `$EXT_DIR` into one of four buckets, then act on each bucket:

| Bucket | Action |
|---|---|
| **A. Already in repo memory** | Delete from external (after content-equivalence verified). |
| **B. About-Peter / user-pref** | Keep in external. These never go in the repo. |
| **C. Active-PR / ephemeral** | Keep in external. Will expire when the PR merges. |
| **D. Durable project rule, NOT in repo** | Migrate to `memory/<bucket>/<name>.md` + INDEX entry, in a new branch + PR. |

### Procedure

1. **Inventory.** `ls "$EXT_DIR"`. Read `$EXT_DIR/MEMORY.md` first — it indexes everything and often pre-classifies files (look for "About Peter / Working Style", "Active PR Working State", "TODO migrate" sections).
2. **Read every `*.md` file** under `$EXT_DIR`. Note each one's `name`, `description`, body, and any `originSessionId`.
3. **Cross-reference against repo memory.** For each external file, search `memory/**/*.md` for a matching atom. Match by *content*, not filename — the naming conventions diverge (external uses `feedback_foo_bar.md`, repo uses `bucket/foo-bar.md`). Diff bodies if the names line up; verify the rule is fully captured before classifying as bucket A.
4. **Classify each file.** Heuristics:
   - **A (delete):** Body is substantively present in a repo atom. The repo atom may be tightened/edited — that's fine, as long as the rule still fires. If the repo version is *weaker* than the external version (drops a key constraint, an exception, a "why"), treat as D and propose a strengthening edit instead of a fresh atom.
   - **B (keep, user-pref):** Talks about Peter's working style, tool preferences (cmd.exe / ReSharper / reforge), interaction rules ("questions aren't directives", "no contradicting options"), or general decision-making heuristics ("sort by value not cost"). Filename usually `feedback_no_*` / `feedback_*_preference` / `user_*`.
   - **C (keep, ephemeral):** Mentions a specific in-flight branch / PR / worktree path / "in progress" state. Filename often `project_issue_<N>_in_progress.md` or `feedback_<branch-specific>.md`.
   - **D (migrate):** Real durable project rule that pre-existed any prior migration sweep. Often architecture / process / code-convention. Frontmatter `type: feedback` plus body that reads like an imperative rule with `Why:` and `How to apply:`.
5. **Build the report.** Per-file table with: filename, classification, reason (one line), proposed action.
6. **Surface the report to Peter and pause.** Do not act without explicit per-bucket approval.
7. **Apply approved actions:**
   - **Bucket A deletions:** `rm "$EXT_DIR/<file>"` per approved entry. Update `$EXT_DIR/MEMORY.md` to drop the corresponding index lines.
   - **Bucket D migrations:** Create a worktree, then `cd` into it before any further work. **Without the `cd`, every subsequent write lands in the main checkout, not the worktree** — that violates this skill's own "always work in a worktree" hard rule and pollutes the user's primary tree.
     ```bash
     cd "$PROJECT_ROOT"
     git fetch origin main
     WT_DIR="$PROJECT_ROOT/.worktrees/migrate-external-memory-<YYYYMMDD>"
     git worktree add -b feat/migrate-external-memory-<YYYYMMDD> "$WT_DIR" origin/main
     cd "$WT_DIR"   # MANDATORY — every later edit/commit assumes cwd is the worktree
     ```
     From inside the worktree, for each migrated rule write `memory/<bucket>/<kebab-case-name>.md` (frontmatter per `memory/META.md`), add the INDEX line (alphabetical within bucket), commit, push, open PR. **Do NOT delete the external file in the same PR** — wait until the PR merges, then delete in a follow-up step (the same way PR 379 was structured).
   - **Buckets B and C:** No action.

### Outputs

- A markdown report (per-file table) included in the assistant message.
- If migrations happen: a PR URL.
- If deletions happen: a one-line summary of what was deleted + the updated MEMORY.md.

---

## Phase 2 — In-repo hygiene scan

### Goal

Find duplication, drift, dead links, and bloat across the repo's project-rule surface. Report → ask → fix.

### Files in scope

- `memory/INDEX.md`
- `memory/META.md`
- `memory/**/*.md` (atoms)
- `CLAUDE.md` (orientation file — should be ~80 lines per META.md target)
- `docs/architecture/design-rules.md` (the constitution)
- `docs/architecture/code-review-rules.md` (reviewer handoff)
- `docs/architecture/coding-rules.md` (stub — verify it's still a stub redirecting to memory/)
- `docs/architecture/data-model.md` (cross-references atoms)
- `docs/sections/*.md` (per-section invariants)

### Checks

Run these and bundle findings into a single report. Each finding gets: severity (BLOCK / IMPORTANT / NIT), location, what's wrong, proposed fix.

1. **Dead INDEX entries.** For each line in `memory/INDEX.md`, verify the linked file exists. Missing → BLOCK.
2. **Orphan atoms.** For each `memory/<bucket>/*.md` file, verify it has an entry in `memory/INDEX.md`. Missing → BLOCK.
3. **Description sanity.** For each atom, verify the `description:` frontmatter exists and is non-empty (BLOCK if missing). Then verify the INDEX one-liner shares at least one substantive noun (≥4 chars, not a stopword) with the atom's frontmatter description — this catches an INDEX line that drifted to describe something the atom no longer does. Do NOT flag mere wording differences: by design the INDEX line is a tighter compression of the atom's description, so they should overlap on key terms but otherwise diverge freely. (Earlier versions of this skill required first-50-char prefix match — that produced ~95% false-positive rate because the compression intentionally rewords. Don't bring that back.)
4. **Bucket misplacement.** Read each atom and check it lives under the right bucket (`architecture/`, `code/`, `process/`, `product/`) per `META.md`'s definitions. Wrong bucket → IMPORTANT.
5. **Content duplication.** Compare every pair of atoms within the same bucket (and cross-bucket for likely conflicts) for substantive overlap. Two atoms saying ~the same thing → IMPORTANT, propose merge or split-of-concerns.
6. **Atom vs constitution drift.** For each atom, search `docs/architecture/design-rules.md` for content that overlaps. If `design-rules.md` covers the same ground:
   - Atom is *consistent* with design-rules → fine, atom is the case-law for the constitution.
   - Atom *conflicts with* design-rules → BLOCK, must reconcile.
   - Atom is *strictly redundant* (verbatim restatement, no new "why" or "how to apply") → NIT, propose deletion or compression to a 1-line pointer.
7. **CLAUDE.md bloat.** Count `wc -l CLAUDE.md`. Target is ~80 lines per `memory/META.md`. Over target by >20% → IMPORTANT, identify candidate sections to atomize. Look for:
   - Bulleted rules under "Critical:" / "Important:" / "Rule:" headings → almost always belong in atoms.
   - Detailed concept sections that fire only on narrow tasks → atomize.
   - Always-on orientation (architecture overview, build commands, terminology, git workflow basics) → keep.
8. **Stale stub.** Verify `docs/architecture/coding-rules.md` is still just a stub redirecting to `memory/INDEX.md`. If content has crept back in → IMPORTANT.
9. **Stale cross-references.** Grep across the repo for references to `coding-rules.md` and any old paths. Anything still pointing at the stub for substantive content (not just acknowledging the redirect) → IMPORTANT.
10. **Atom size.** Atoms over ~80 lines are likely narratives that belong in `design-rules.md` instead. NIT, suggest move.
11. **Section invariants alignment.** For each `docs/sections/*.md`, check that any rules it cites by name exist in the repo (atoms or design-rules sections). Dead citations → IMPORTANT.

### Outputs

- A consolidated markdown report grouped by severity.
- After Peter approves a subset, apply the fixes in a new worktree + branch + PR. Use the same `git worktree add` + **mandatory `cd`** pattern as Phase 1's Bucket D migrations — without the `cd` into the worktree, edits land in the main checkout.
- BLOCK findings should be queued first; IMPORTANT next; NIT only if Peter explicitly opts in.

---

## End-to-end pacing

Default flow with no arguments:

1. Run Phase 1 inventory + classification → present report → pause.
2. After approval, execute Phase 1 actions (worktree + migrations PR; deletions queued for after merge).
3. Run Phase 2 audit → present report → pause.
4. After approval, execute Phase 2 fixes in a separate worktree + PR (do not stack with the Phase 1 PR — they touch overlapping files and should not race).
5. Once both PRs land, return to Phase 1 and complete the deletion of duplicates that were just migrated.

If `report-only`, stop after each phase's report — never propose or apply changes.

## What this skill is NOT

- Not a place to *invent* new rules. If the conversation surfaces a new rule, capture it via the normal flow (`memory/process/rules-maintenance.md`), not via this skill.
- Not a CLAUDE.md rewrite. Trimming CLAUDE.md is a Phase 2 *finding*; the rewrite happens in a separate, focused PR after Peter sees the proposal.
- Not a doc-freshness sweep — that's `/freshness-sweep`. This skill cares about the *project-rule* surface (what rules exist and where), not the drift-prone reference docs.
