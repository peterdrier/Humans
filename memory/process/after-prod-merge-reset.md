---
name: After production merge, reset peter's `main` to upstream
description: When a PR to `nobodies-collective/Humans` lands, fetch upstream and `git reset --hard upstream/main` on origin's `main`, then `git push origin main --force-with-lease`. Keeps the fork aligned with prod.
---

After a PR to `nobodies-collective/Humans` lands and is merged to upstream `main`, reset peter's fork's `main` to match:

```bash
git fetch upstream main
git checkout main && git reset --hard upstream/main
git push origin main --force-with-lease
```

**Why:** Peter's fork is the QA branch (Coolify auto-deploys from it). After production promotion, the fork's `main` will diverge from upstream because the squashed/rebased commits on upstream have different SHAs than the per-PR commits on the fork. Without the reset, the next batch promotion includes "ghost" commits that look like new work but are already on upstream.

**How to apply:**

- Run after every successful upstream merge — don't batch these resets.
- `--force-with-lease` (not `--force`) — this fails safely if someone else pushed to `origin/main` in the meantime.
- Only the fork (`origin`) needs this reset; upstream is already authoritative.
- Don't reset upstream — the fork tracks upstream, never the reverse.

**Related:** `CLAUDE.md` "Git Workflow" section for the broader two-remote workflow.
