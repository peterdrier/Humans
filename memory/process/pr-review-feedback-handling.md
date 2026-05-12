---
name: PR review feedback — fetch from both repos, reply per-thread, resolve when authorized
description: When handling PR review feedback (Codex, Claude, human reviewers), fetch comments from BOTH repos via the inline-comments API, reply in each finding's own thread, resolve threads when Peter-authorized declines, never ping `@codex review` to re-trigger.
---

When handling PR review feedback — Codex bot, Claude bot, or human inline review comments — these four rules fire together. They cover the full "find → triage → reply → close" loop.

## 1. Pull comments from BOTH repos, top-level AND inline

The default `gh pr view --json comments` returns **top-level** comments only. Inline review comments require a separate API call. Missing this has lost real findings on PRs #208 and #203.

```bash
gh pr view <N> --repo <owner>/Humans --json comments        # top-level
gh api repos/<owner>/Humans/pulls/<N>/comments              # inline review threads
gh api repos/<owner>/Humans/pulls/<N>/reviews               # formal reviews
```

Run all three against BOTH `peterdrier/Humans` and `nobodies-collective/Humans` — Codex sometimes posts on the fork even when the active PR is on upstream, and vice versa.

## 2. Reply in each finding's own thread, not as a top-level comment

After pushing a fix for a review comment, reply via:

```bash
gh api -X POST repos/<owner>/<repo>/pulls/<N>/comments/<comment_id>/replies \
  -f body="<one-line fix summary + fix commit SHA>"
```

Do NOT rely on a single top-level `gh pr comment` as the acknowledgement.

**Why:** A top-level comment doesn't mark each individual finding's thread as addressed — it's not obvious from scrolling which findings got acknowledged or fixed vs. missed. Per-thread replies make the fix trail auditable finding-by-finding, and GitHub's "resolved" UX works per thread.

When the same finding is restated on a later commit (duplicate inline comment on a fix push), reply to BOTH threads. A top-level summary may sit ON TOP of thread replies, never instead of them.

## 3. Resolve threads when Peter has authorized the deviation

When triaging a finding as "decline because Peter authorized" (budget bumps acknowledged in the PR body, "leave at N" answer to `AskUserQuestion`, etc.), **resolve the thread** in the same step as posting the reply.

The decision rule:

| Reply | Action |
|---|---|
| "Not changing — Peter authorized X" | Reply + **resolve thread** |
| "Not changing — bot is technically wrong" | Reply, **leave open** (default `/pr-fix` behavior) |

Discovered on PR #448 when the bot flagged the same authorized budget bump twice and Peter had to ask for the second thread to be manually resolved. The default leave-open rule exists so reviewers can push back when Claude judged the bot wrong — that purpose is moot once Peter has explicitly sanctioned the deviation.

## 4. Never ping `@codex review` to re-trigger

After pushing fixes to a PR, **don't** post `@codex review` or `@claude please re-review` comments.

**Why:** Codex has limited quota — extra rounds burn it for no gain. Claude reviews are automatic on push (`synchronize` trigger), so re-pinging is redundant.

When working a PR review fix-loop: push the fix → thread-reply to each finding → stop. Don't add a "please re-review" comment. The Claude bot picks up the new commit on its own; if Codex doesn't, that's intentional.

**Related:** [`.github/workflows/claude-review.yml`](../../.github/workflows/claude-review.yml) — auto-review fires on `synchronize`; the prompt fetches existing thread state so re-flags don't accumulate.
