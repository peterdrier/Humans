# How `memory/` Works

A flat-ish catalog of atomic project rules. Each rule lives in its own file with frontmatter + a `Why:` / `How to apply:` body. `INDEX.md` is the on-demand catalog Claude consults when a task needs to check project rules — not auto-loaded itself. `CLAUDE.md` (which IS auto-loaded) tells Claude when and why to read `INDEX.md`, and Claude then reads the specific atom on demand.

## Design intent

Two goals drive the structure, and both are non-negotiable:

1. **Token efficiency.** Only `CLAUDE.md` (~80 lines, orientation + pointers) is paid every turn. `INDEX.md` is a 1-read scan when a rule might apply; the atom body is a 2nd read when one does. The old long-bullet `CLAUDE.md` paid context for every rule on every turn, even when 95% were irrelevant to the current task.
2. **Portability across machines.** All rules live in the repo and sync via git across Peter's Windows / NUC / laptop. The previous external Claude memory at `~/.claude/projects/H--source-Humans/memory/` was per-machine — a rule learned in one session was invisible from any other machine. That's the bug this directory exists to fix; preserve it. New rules go here, not there.

## What this replaces

- The external Claude memory system (per-machine, didn't sync across Peter's Windows / NUC / laptop).
- Long bullet sections in `CLAUDE.md` (paid context every turn even when irrelevant).
- Long bullet sections in `coding-rules.md` (not auto-loaded; rules buried in prose).

## When to add a rule here vs. somewhere else

| Where | What goes there |
|---|---|
| **`memory/<bucket>/<rule>.md`** (this directory) | Atomic, task-fires rules: "when doing X, do Y". One rule per file (or one cluster of tightly related sub-rules per file). |
| **`docs/architecture/design-rules.md`** | Architectural narrative. The "constitution." Read sequentially by new contributors and reviewers. Layer responsibilities, table ownership, the §15 caching story. Don't atomize — atomization destroys the story. |
| **`docs/architecture/code-review-rules.md`** | Reviewer handoff. Passed verbatim to Codex / Gemini / Claude as one block. Don't split. |
| **`docs/sections/*.md`** | Per-section invariants. One file per section is already the right granularity. |
| **`CLAUDE.md`** | Orientation only — purpose, layer overview, terminology, build commands, Git basics, pointer to `memory/INDEX.md`. The rules-of-thumb that fire **every turn** in **every conversation**. ~80 lines max. Anything that fires only when touching a specific area belongs in an atom, not here. |

**The mental model:** `design-rules.md` is the constitution. `memory/*.md` are the case law. `CLAUDE.md` is the table of contents.

## Buckets

Atoms live under one of four buckets. Pick by *primary* purpose:

- **`architecture/`** — system-level rules about how the code is shaped (interface budgets, drop-storage discipline, layer constraints, no-startup-guards, etc.)
- **`code/`** — code-level conventions and patterns (naming, idioms, NodaTime, JSON serialization, EF query gotchas, view-component vs partial, etc.)
- **`process/`** — workflow/git/PR/issues/release/triage (issue-ref qualifying, PR review process, dotnet verbosity, no-direct-to-main, etc.)
- **`product/`** — terminology, restrictions, framing, deployment specifics (Humans terminology, Coolify build constraint, Shared Drives only, voting-not-prominent, etc.)

If a rule could go in two buckets, pick the one a future-you would search first. Cross-link from the other if useful.

## File format

Filename: kebab-case, descriptive enough to grep (`no-extensions-for-owned-classes.md`, not `extensions.md`).

Frontmatter:

```yaml
---
name: <human-readable name — same wording you'd use in INDEX.md>
description: <one-line trigger telling future-you when to read this — what + when>
---
```

Body structure:

```markdown
<The rule itself, in one or two imperative sentences.>

**Why:** <The reason. Often a past incident, an architectural commitment, or a stakeholder preference. This is what lets future-you judge edge cases instead of blindly following.>

**How to apply:** <When/where this fires. Examples of what it looks like in practice. What NOT to do.>
```

Optional sections: `**Exceptions:**`, `**Related:**` (links to other atoms or to design-rules sections), `**Examples:**`.

Keep atoms short — one screen ideally. If a rule needs more than that, it's probably a narrative and belongs in `design-rules.md` instead.

## Adding a new rule

1. Decide bucket. Pick a kebab-case filename.
2. Write the atom file with frontmatter + Why + How to apply.
3. **Add a line to `INDEX.md`** under the right bucket section. The line format is:
   ```markdown
   - [`<filename-without-md>`](<bucket>/<filename>) — <description from frontmatter>
   ```
4. If the rule supersedes a memory in `~/.claude/projects/H--source-Humans/memory/`, leave that memory alone — it's per-machine and doesn't sync. The atom in the repo is now the source of truth; the external memory becomes dead weight to clean up later.

## Updating an existing rule

Edit the atom file. Update the `description` in INDEX.md if the trigger changed. If the rule no longer applies, delete the atom file AND its INDEX.md line in the same commit.

## Why not just a flat folder

The bucket subfolders are a convenience for human navigation, nothing more. Atoms are retrieved by `Glob "memory/**/*<keyword>*.md"` or by reading the INDEX. The subfolders don't carry semantic meaning beyond "primary lens to search by."
