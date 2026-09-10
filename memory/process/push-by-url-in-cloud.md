---
name: push-by-url-in-cloud
description: In a Claude Code cloud container, push to the repository URL, never to a remote name — `origin` is re-bound to nobodies-collective/Humans (production) behind the agent, mid-session and more than once.
---

In a Claude Code cloud run (`CLAUDE_CODE_REMOTE=true`), push with the URL:

```bash
git push https://github.com/peterdrier/Humans.git <branch>
```

**Why:** the container starts with `origin` = `nobodies-collective/Humans` (production), contradicting `AGENTS.md`, and re-applies that configuration mid-session — across a wake and without one, more than once in a single run. A Phase-0 `git remote set-url` does not hold, and checking `git remote -v` in the same command as the push does not help either: the name is re-bound behind the agent, and a check-then-push chain pushed a doctor branch to production twice (peterdrier/Humans#1586, findings 33 and 40). A URL cannot be redirected by a remote reconfiguration.

**How to apply:**
- Push to the URL, never to `origin`, for every push in a cloud run — the first one included.
- A push to a URL never updates the `origin/<branch>` remote-tracking ref, so any check comparing `HEAD` to it (a stop hook, `git status`) reports the pushed commit as unpushed after every URL push, whether or not `origin` is bound correctly. That is a stale ref, not a failed push: repointing `origin` does not silence it and re-pushing is wasted motion. Confirm with `git ls-remote https://github.com/peterdrier/Humans.git <branch>`, then `git fetch origin <branch>` to bring the tracking ref forward.
- On a local machine this does not apply; `origin` there is what `git remote -v` says it is.

Related: [[cross-repo-pr-push-target]], [[always-use-worktree]].
