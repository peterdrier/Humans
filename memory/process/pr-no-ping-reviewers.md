---
name: Don't ping PR reviewers — Codex burns quota, Claude auto-reviews
description: After pushing fixes to a PR, never post `@codex review` or `@claude please re-review`. Codex quota is limited; Claude reviews on push automatically.
---

After pushing fixes to a PR, **don't** post `@codex review` or `@claude please re-review` comments.

**Why:** Codex has limited quota — extra rounds burn it for no gain. Claude reviews are automatic on push, so re-pinging is redundant.

**How to apply:** When working a PR review fix-loop:
- Push the fix → thread-reply to each finding → stop.
- Don't add a "please re-review" or "@codex review" comment.
- Claude bot will pick up the new commit on its own; if Codex doesn't, that's intentional.
- Still wait/check for fresh reviews if needed before declaring "Codex-clean" — just let them come naturally.
