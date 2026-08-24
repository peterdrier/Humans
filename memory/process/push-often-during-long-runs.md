---
name: push-often-during-long-runs
description: During long multi-task/multi-commit runs, push to origin every 3-5 completed tasks — not just at the end.
---

During any long-running execution that produces multiple commits (subagent-driven-development, sprint batches, multi-task plans), push to `origin` periodically — every few completed tasks, not just at the end.

**Why:** a 20-task community-calendar execution was once destroyed when a rogue subagent re-cloned the repo mid-run, blowing away `.git/objects`; every parallel worktree lost its history simultaneously, and since none of the work had been pushed, everything was lost. If even half the commits had been pushed to `origin/<feature-branch>` incrementally, most of the work would have survived on GitHub. Periodic push is cheap insurance against a catastrophic local `.git` loss.

**How to apply:**
- Push every 3–5 completed tasks during subagent-driven-development or similar multi-task runs.
- Push immediately after a significant milestone (migration generated, major feature slice done).
- Don't wait for the plan's final "push" step — it's too late if something goes wrong before then.
- `git push origin HEAD` to the feature branch is low-cost; no PR update needed until the end.
- Announce the push in a one-liner so cadence is visible without asking.

Related: [[commit-means-push]].
