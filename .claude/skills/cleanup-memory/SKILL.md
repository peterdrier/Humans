---
name: cleanup-memory
description: "Two-phase memory hygiene. Phase 1: scan external Claude Code memory (~/.claude/projects/<slug>/memory/) for durable rules to migrate into the repo and entries already duplicated by repo atoms. Phase 2: audit in-repo memory/, CLAUDE.md, docs/architecture/, and docs/sections/ for dead links, duplication, drift, and bloat."
argument-hint: "[external] [repo] [report-only]"
---

# Cleanup Memory

Two surfaces:
- **External** — `~/.claude/projects/<slug>/memory/` (per-machine; never hardcode the path)
- **Repo** — `memory/`, `CLAUDE.md`, `docs/architecture/design-rules.md`, `docs/architecture/code-review-rules.md`, `docs/sections/`

Repo `memory/` is the source of truth for durable project rules (syncs via git). External holds only about-Peter / user-pref entries and active-PR working state. Read `memory/META.md` before working.

## Arguments

- *(none)* — both phases: external → repo → consolidated report → ask → execute
- `external` — Phase 1 only
- `repo` — Phase 2 only
- `report-only` — full report, no changes

## Hard Rules

- **Never hardcode the external memory path.** Derive at runtime (see below).
- **Never delete a file before confirming the rule survives somewhere.** Diff content; don't match by filename alone.
- **Always work in a worktree.** `.worktrees/cleanup-memory-<date>`. Never edit the main checkout.
- **Migration PRs never modify the same file twice.** Add atom (PR A), merge, then consolidate (PR B).

## Discovering the External Memory Directory

Slug = absolute git root with `:`, `/`, `\` replaced by `-`. Example: `H:\source\Humans` → `H--source-Humans`.

```bash
PROJECT_ROOT="$(git rev-parse --show-toplevel)"
REPO_NAME="$(basename "$PROJECT_ROOT")"
EXT_DIR=""
for d in "$HOME"/.claude/projects/*/memory; do
  [ -f "$d/MEMORY.md" ] || continue
  parent_slug="$(basename "$(dirname "$d")")"
  if echo "$parent_slug" | grep -qi "$REPO_NAME" \
     || grep -qiE "(humans|nobodies)" "$d/MEMORY.md" 2>/dev/null; then
    EXT_DIR="$d"; break
  fi
done
if [ -z "$EXT_DIR" ]; then
  SLUG="$(printf '%s' "$PROJECT_ROOT" | sed 's|[:/\\]|-|g')"
  candidate="$HOME/.claude/projects/$SLUG/memory"
  [ -d "$candidate" ] && EXT_DIR="$candidate"
fi
```

If still empty, ask Peter — do not guess. Phase 1 has nothing to do on a fresh machine; skip to Phase 2.

---

## Phase 1 — External Memory Scan

Classify every file in `$EXT_DIR`:

| Bucket | Criteria | Action |
|--------|----------|--------|
| **A. Already in repo** | Body substantively present in a repo atom (verify content, not filename) | Delete from external after Peter approves |
| **B. About-Peter / user-pref** | Working style, tool preferences, interaction rules | Keep |
| **C. Active-PR / ephemeral** | Mentions in-flight branch/PR/worktree or "in progress" state | Keep |
| **D. Durable rule, not in repo** | Imperative rule with `Why:`/`How to apply:`, architecture/process/code-convention | Migrate |

If a repo atom is weaker than the external version (drops a constraint or "why"), treat as D and propose strengthening the atom — don't just mark it A.

### Procedure

1. `ls "$EXT_DIR"`. Read `MEMORY.md` first — it indexes everything and often pre-classifies files.
2. Read every `*.md` under `$EXT_DIR`.
3. Cross-reference against `memory/**/*.md` by *content* (naming conventions diverge: external uses `feedback_foo_bar.md`, repo uses `bucket/foo-bar.md`).
4. Build a per-file table: filename | bucket | reason | proposed action. Surface it to Peter and pause.
5. Apply approved actions:
   - **A deletions:** `rm "$EXT_DIR/<file>"`. Update `$EXT_DIR/MEMORY.md` to drop index lines.
   - **D migrations:** Create worktree, **`cd` into it** (mandatory — without this, edits land in the main checkout), then write `memory/<bucket>/<kebab-name>.md` (frontmatter per `memory/META.md`), add INDEX line (alphabetical within bucket), commit, push, open PR. Do NOT delete the external file in the same PR — wait until the migration PR merges.

---

## Phase 2 — In-Repo Hygiene Scan

### Files in Scope

`memory/INDEX.md`, `memory/META.md`, `memory/**/*.md`, `CLAUDE.md`, `docs/architecture/design-rules.md`, `docs/architecture/code-review-rules.md`, `docs/architecture/coding-rules.md`, `docs/architecture/data-model.md`, `docs/sections/*.md`

### Checks

Each finding: severity (BLOCK / IMPORTANT / NIT), location, what's wrong, proposed fix.

1. **Dead INDEX entries.** Every `memory/INDEX.md` line → verify file exists. Missing → BLOCK.
2. **Orphan atoms.** Every `memory/<bucket>/*.md` → verify INDEX entry exists. Missing → BLOCK.
3. **Description sanity.** Each atom needs non-empty `description:` frontmatter (BLOCK if missing). INDEX one-liner must share at least one substantive noun with the atom's description — catches drift. Do NOT flag wording differences; by design the INDEX line is a tighter compression.
4. **Bucket misplacement.** Atom lives under wrong bucket (`architecture/`, `code/`, `process/`, `product/`) per `META.md` definitions → IMPORTANT.
5. **Content duplication.** Same-bucket pairs (and likely cross-bucket conflicts) with substantive overlap → IMPORTANT, propose merge or split.
6. **Atom vs constitution drift.** For each atom, check against `docs/architecture/design-rules.md`:
   - Consistent → fine
   - Conflicts → BLOCK
   - Strictly redundant (verbatim, no new "why"/"how to apply") → NIT, propose deletion or 1-line pointer
7. **CLAUDE.md bloat.** Target ~80 lines (per `memory/META.md`). Over by >20% → IMPORTANT. Candidates to atomize: bulleted rules under "Critical:"/"Important:"/"Rule:" headings, detailed concept sections for narrow tasks. Keep: architecture overview, build commands, terminology, git workflow basics.
8. **Stale stub.** `docs/architecture/coding-rules.md` should be a redirect-only stub to `memory/INDEX.md`. Any crept-in content → IMPORTANT.
9. **Stale cross-references.** Grep for `coding-rules.md` references pointing at it for substantive content → IMPORTANT.
10. **Atom size.** Atoms >80 lines likely belong in `design-rules.md` → NIT.
11. **Section invariants alignment.** Each `docs/sections/*.md` — dead citations to atoms or design-rules sections → IMPORTANT.

### Outputs

Consolidated report grouped by severity → present to Peter → apply approved fixes in a new worktree + branch + PR (same `git worktree add` + mandatory `cd` pattern as Phase 1). BLOCK first; IMPORTANT next; NIT only if Peter opts in.

---

## End-to-End Pacing (no arguments)

1. Phase 1 inventory + classification → report → pause
2. Execute Phase 1 approved actions (migration PR; deletions queued post-merge)
3. Phase 2 audit → report → pause
4. Execute Phase 2 fixes in a separate worktree + PR (don't stack with Phase 1 — overlapping files)
5. After both PRs land, complete Phase 1 deletions of migrated duplicates

`report-only`: stop after each phase's report.

## What This Skill Is NOT

- Not for inventing new rules — use `memory/process/rules-maintenance.md`
- Not a CLAUDE.md rewrite — Phase 2 finds candidates; the rewrite is a separate focused PR
- Not a doc-freshness sweep — that's `/freshness-sweep`
