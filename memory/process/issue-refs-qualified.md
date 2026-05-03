---
name: Always qualify GitHub issue/PR refs with repo
description: Two repos with overlapping issue numbers. Bare #N is ambiguous — always write `peterdrier#N` (fork) or `nobodies-collective#N` (upstream), and pass `--repo` to every `gh` call.
---

Always write GitHub issue and PR references as `owner/repo#N` or at minimum `owner#N` — never bare `#N`.

- Fork (default for QA, small changes, peter's own backlog): `peterdrier#292` (or `peterdrier/Humans#292`)
- Upstream (production, nobodies-collective): `nobodies-collective#586` (or `nobodies-collective/Humans#586`)

Applies everywhere: commit messages, PR bodies, issue comments, chat responses, triage output, sprint plans, release notes, `todos.md` entries, `gh` commands.

**Why:** The two repos have overlapping issue numbers. A bare `#310` is ambiguous and has caused real chaos — wrong issues closed, wrong commits linked, PR descriptions pointing at the other repo's tracker. Peter has had to untangle this repeatedly.

**How to apply:**

- Before writing any issue/PR number, decide which repo it belongs to, then prefix it. If you don't know, ask — don't guess.
- When running `gh issue view/close/list/edit` or `gh pr view/list/comment`, always include `--repo <owner>/Humans`. Never rely on the ambient default.
- When drafting issue bodies that cross-reference others, use the qualified form there too.
