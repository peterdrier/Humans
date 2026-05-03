---
name: Thread-reply to each Codex finding directly
description: After fixing a PR review comment, post the acknowledgement in that comment's own thread (POST /pulls/{n}/comments/{id}/replies), not as a top-level PR comment. Auditable per-finding.
---

When Codex (or any review bot / human reviewer) leaves inline comments on a PR, reply to each comment's thread directly via:

```bash
gh api -X POST repos/{owner}/{repo}/pulls/{pr}/comments/{comment_id}/replies -f body="..."
```

Do NOT rely on a single top-level PR comment (`gh pr comment`) as the acknowledgement.

**Why:** A top-level comment doesn't mark the individual finding's thread as addressed — it's not obvious from scrolling which findings got acknowledged or fixed vs. missed. Per-thread replies make the fix trail auditable finding-by-finding and let GitHub's "resolved" UX work per thread.

**How to apply:**

- After pushing a fix for a review comment, post a reply in that comment's own thread with: one-line fix summary + ref to the fix commit.
- When Codex restates the same finding after a later push (duplicate comment on a later commit), reply to BOTH threads.
- A top-level summary comment can sit ON TOP of thread replies, not instead of them. (But don't `@codex review` to re-trigger — see [`pr-no-ping-reviewers`](pr-no-ping-reviewers.md).)
- Applies to every agent dispatch that addresses PR comments — the dispatch prompt must name the specific comment IDs and require per-thread replies.
