---
name: commit-means-push
description: When a workflow step says "commit and push", run the push in the same turn — don't stop after `git commit` waiting for confirmation.
---

When a skill or workflow step says "commit and push" (a PR fix loop, end-of-session wrap-up, etc.), run `git push` immediately after the commit, in the same turn. Don't pause after `git commit` waiting for implicit confirmation — the expectation is that the work is on the remote when the step finishes.

**Why:** Stopping mid-step cost Peter real time on a PR once — a commit sat local while CI re-runs and reviewer cycles waited on a push that should have happened one tool call later.

**How to apply:**
- After any `git commit` that's part of a "commit and push" or "land it" intent, run `git push` next, in the same response.
- If the push needs `--force` or `--force-with-lease`, that's the one case to stop and ask first. Normal pushes never need a confirmation gate.
- After pushing, post any per-thread / top-level PR replies without waiting — that's also part of the same step, not a separate session.
- "I committed" is not an end-of-turn summary. The unit of work is "pushed (and threads replied)", not "commit exists locally".
