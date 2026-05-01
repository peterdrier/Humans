---
name: Update todos.md and close GitHub issues after committing resolving work
description: After commit: move resolved items in `todos.md` to the Completed section with summary + commit hash. Close resolved GitHub issues with `gh issue close <N> -c "comment"` including a brief summary and commit hash.
---

After committing work that resolves or partially resolves items in `todos.md`, update the file: move completed items to the Completed section with a summary of what was done and the commit hash.

After committing work that resolves a GitHub issue, close it:
```bash
gh issue close <number> --repo <owner>/Humans -c "comment with summary + commit SHA"
```

**Why:** Stale `todos.md` entries clutter every session's planning step. Open GitHub issues that are actually shipped mislead triage.

**How to apply:**

- `todos.md` updates: same commit as the work, or immediately after if the change touched the SHA you want to reference.
- `gh issue close`: include the qualified repo flag (`peterdrier/Humans` or `nobodies-collective/Humans` per [`issue-refs-qualified`](issue-refs-qualified.md)).
- Closing comment should name what shipped and the commit SHA — not just "fixed."
