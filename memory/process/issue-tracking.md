---
name: Close GitHub issues after committing resolving work
description: After commit: close resolved GitHub issues with `gh issue close <N> -c "comment"` including a brief summary and commit hash.
---

After committing work that resolves a GitHub issue, close it:
```bash
gh issue close <number> --repo <owner>/Humans -c "comment with summary + commit SHA"
```

**Why:** Open GitHub issues that are actually shipped mislead triage. GitHub issues are the only backlog — there is no local todo file.

**How to apply:**

- `gh issue close`: include the qualified repo flag (`peterdrier/Humans` or `nobodies-collective/Humans` per [`issue-refs-qualified`](issue-refs-qualified.md)).
- Closing comment should name what shipped and the commit SHA — not just "fixed."
