# How `memory/` Works

A catalog of atomic project rules. Each rule is one file with frontmatter + a `Why:` / `How to apply:` body. `INDEX.md` lists every atom in one line and is read on demand — when a task might touch a rule — not on every turn. `AGENTS.md` (auto-loaded via `CLAUDE.md`) says when to scan it; the atom body is the second read when a line's trigger matches.

## Design intent

1. **Token efficiency.** Only `CLAUDE.md` → `AGENTS.md` and Peter's two rules files are paid every turn. `INDEX.md` is one read when a rule might apply; an atom is a second. An INDEX line is a *trigger*, not the rule — keep it to one sentence so the scan stays cheap.
2. **Portability across machines.** Rules live in the repo and sync via git across Peter's machines and the cloud runners. Per-machine agent memory (`~/.claude/projects/<slug>/memory/`) does not sync; new durable rules go here, never there.

## Where a rule belongs

| Where | What goes there |
|---|---|
| **`memory/<bucket>/<rule>.md`** | Atomic, task-fired rules: "when doing X, do Y". One rule per file. |
| **`docs/architecture/peters-hard-rules.md`**, **`peters-working-rules.md`** | Peter's constitution. Hand-written; LLMs never edit them. |
| **`docs/architecture/design-rules.md`** | The regulations: implementing detail behind the hard rules, read one section at a time. Narrative, not atomized. |
| **`docs/architecture/code-review-rules.md`** | Reviewer handoff, passed verbatim to review bots. Don't split. |
| **`src/Sections/Humans.<Section>/Docs/<Section>.md`** | Per-section invariants. |
| **`AGENTS.md`** | Orientation: purpose, glossary, layer overview, build commands, PR flow, pointers. Only what fires every turn in every conversation. |
| **`.claude/hooks/session-start.sh`** | Rules a harness default actively contradicts, restated at session start. Keep it to those. |

**Mental model:** the hard rules are the constitution, `design-rules.md` the regulations, `memory/*.md` the case law, `AGENTS.md` the table of contents.

### What `memory/` is NOT for

- **Not ADRs or design narrative.** "We chose X over Y because Z" belongs in `design-rules.md` or a dated doc under `docs/`, with human review.
- **Not session ephemera.** In-flight notes and task state die with the conversation.
- **Not "might be useful someday."** Every atom taxes every INDEX scan. If you can't say in one line when a future task needs it to fire, it doesn't belong yet.

## Buckets

- **`architecture/`** — how the code is shaped: layer constraints, data ownership, interface budgets.
- **`code/`** — code-level conventions: naming, idioms, NodaTime, EF gotchas, views.
- **`process/`** — workflow: git, PRs, issues, review handling, agent operation.
- **`product/`** — terminology, restrictions, deployment specifics.

Pick the bucket a future reader would search first; the subfolders are a navigation aid, nothing more.

## File format

Filename: kebab-case, descriptive enough to grep.

```yaml
---
name: <human-readable name>
description: <one sentence, ≤180 chars: the trigger — when to read this. Same text as the INDEX line.>
---
```

```markdown
<The rule, in one or two imperative sentences.>

**Why:** <The incident, commitment, or preference behind it — enough to judge edge cases.>

**How to apply:** <When it fires, what it looks like, what not to do.>
```

Optional: `**Exceptions:**`, `**Related:**` (`[[atom-name]]` links), `**Examples:**`. Keep an atom to one screen; longer means it is a narrative and belongs in `design-rules.md`.

## Adding, changing, removing

1. Write the atom with frontmatter + Why + How to apply.
2. Add its INDEX line under the bucket, same commit: `` - [`<name>`](<bucket>/<name>.md) — <description> ``.
3. Changing a trigger: update both the frontmatter and the INDEX line. Retiring a rule: delete the file and its INDEX line together.

A change confined to `memory/**` may go straight to `origin/main` ([`no-direct-to-main`](process/no-direct-to-main.md) has the carve-out).
