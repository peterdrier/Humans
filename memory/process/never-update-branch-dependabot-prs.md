---
name: never-update-branch-dependabot-prs
description: Never run `gh pr update-branch` or push anything on a Dependabot-owned PR branch — Dependabot sees a foreign edit, refuses to rebase, and closes the PR.
---

**Never run `gh pr update-branch` (or push anything) on a Dependabot-owned PR branch.** Dependabot detects the branch was modified by someone else, refuses to rebase, and **closes the PR** ("Looks like this PR has been edited by someone other than Dependabot… you can request `@dependabot recreate`").

**Why:** during one CI-unwedging pass, several Dependabot PRs hadn't picked up a `@dependabot rebase` comment. Using `gh pr update-branch` to force them along killed two of them outright — both closed unmerged, so those version bumps silently didn't ship while the rest of the batch merged. A third survived only by luck (the update-branch call reported "already up-to-date" and pushed nothing).

**How to apply:**
- To refresh a Dependabot PR, comment `@dependabot rebase` and wait — it can take several minutes. Do not escalate to `update-branch`.
- If a rebase genuinely won't take, comment `@dependabot recreate` (regenerates from scratch) — never push.
- `gh pr update-branch` is fine for human/agent-owned branches; the hazard is specific to bot-owned branches.
- When a Dependabot PR can't build because of an exact-pin companion package, don't try to fix it in place either — open a coordinated PR and close the Dependabot one as superseded.
