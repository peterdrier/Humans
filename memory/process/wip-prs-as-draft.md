---
name: wip-prs-as-draft
description: Open multi-phase / mid-implementation PRs as `--draft` so CI and bot review (Claude review, Codex) don't burn compute on every intermediate push.
metadata:
  type: process
---

# WIP PRs open as draft

When opening a PR that will receive multiple intermediate pushes before it's review-ready — section-align runs, multi-phase plans, anything where pushes will land throughout the run before the work is actually done — use `gh pr create --draft`.

**Why:** GitHub Actions (build, claude-review, Codex review) trigger on every push, even to draft PRs depending on workflow config — but the project's review bots are configured to skip drafts. Opening "ready" and accumulating 10+ phase pushes burns compute and floods the review feed with stale comments that get superseded by later commits. Peter has manually flipped non-draft WIP PRs back to draft mid-run to stop the waste.

**How to apply:** When `gh pr create` is for work that isn't complete-and-ready at the moment of opening (section-align, multi-phase refactor, mid-implementation), add `--draft`. Flip to ready-for-review (`gh pr ready <n>`) only at the end of the run, when the final state is what you'd want a reviewer to look at.

**Exceptions:** One-shot PRs (single commit, single push, immediately review-ready) open normally. The rule is about *intermediate* pushes, not the existence of follow-up work.

See [[no-direct-to-main]] for the broader workflow context.
