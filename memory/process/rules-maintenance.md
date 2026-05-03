---
name: When you learn a new project rule, write an atom + update INDEX in the same commit
description: Discovered rules become atom files under `memory/<bucket>/` with frontmatter + Why/How-to-apply, plus a one-line entry in `INDEX.md`. Same commit. Don't rely on external memory for project rules.
---

When a new project rule surfaces during a conversation — Peter corrects a pattern, an incident produces a rule, a "from now on" emerges — capture it as a `memory/` atom **in the same commit as the work that discovered it**, and add a line to `INDEX.md`. Do not rely on per-machine external memory for project rules.

**Why:** External memories don't sync across Peter's machines (Windows, NUC, laptop) — that's why this whole system exists. A rule that lives only in external memory becomes drift between machines. A rule in `memory/` is git-versioned, syncs everywhere, reviewable on PR, and grep-able by every Claude session that opens the repo.

**How to apply:**

1. **Bucket it.** Pick `architecture/`, `code/`, `process/`, or `product/` based on primary lens. See [`META.md`](../META.md).
2. **Filename.** Kebab-case, descriptive enough to grep (`no-extensions-for-owned-classes.md`, not `extensions.md`).
3. **Frontmatter.** `name` + `description` (the description shows up in `INDEX.md` — make it a clear *trigger*, not just a title).
4. **Body.** The rule itself in 1-2 imperative sentences, then `**Why:**` (the reason — past incident, architectural commitment, stakeholder ask), then `**How to apply:**` (when/where this fires, with examples). Same shape as every other atom in the catalog.
5. **INDEX entry.** Add one line under the bucket section in [`INDEX.md`](../INDEX.md): `- [`name`](bucket/name.md) — description`.
6. **Commit it with the work that triggered it.** Don't batch.

If a rule is ambiguous or contested, write it down as a draft and ask Peter to review the wording. A wrong-but-written rule is fixable; an unwritten rule decays and re-litigates every session.

**For periodic catch-up sweeps** of memories that accumulated in `~/.claude/projects/H--source-Humans/memory/` between sessions on different machines, use the `/memory-refresh` skill (TBD — pending).

**Related:** [`META.md`](../META.md) for full pattern documentation.
