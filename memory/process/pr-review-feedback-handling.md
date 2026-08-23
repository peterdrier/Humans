---
name: PR review feedback — fetch from both repos, reply per-thread, resolve when authorized
description: When handling PR review feedback (Codex, Claude, human reviewers), fetch comments from BOTH repos via the inline-comments API, reply in each finding's own thread, resolve every dispositioned thread (fixed, refuted, wontfix, issue opened), react 👍/👎 on every Codex finding, never ping `@codex review` to re-trigger, and post a follow-up comment closing the loop on any top-level (threadless) review.
---

When handling PR review feedback — Codex bot, Claude bot, or human inline review comments — these five rules fire together. They cover the full "find → triage → reply → close" loop.

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

## 3. Resolve every dispositioned thread — fixed or refuted

Once a finding has its disposition reply — fixed, refuted, wontfix, issue opened — **resolve the thread** in the same step. Unresolved threads stay open forever and make a worked PR look unworked.

| Reply | Action |
|---|---|
| "Fixed in `<sha>`" | Reply + **resolve thread** |
| "Not changing — `<reason>`" (Peter authorized, bot factually wrong, out of scope, wontfix) | Reply + **resolve thread** |
| "Opened `<owner>#N`" | Reply + **resolve thread** |

The only thread left open is one still waiting on someone — a genuine open question or a live disagreement Peter hasn't ruled on. Resolve in the same step as the reply, never as a later cleanup pass.

Discovered on PR #448 (bot re-flagged an authorized deviation until the thread was manually resolved); refuted-thread resolution confirmed by Peter on PR #1460 — a refutation reply without a resolve still leaves the thread open.

## 4. Never ping `@codex review` to re-trigger

After pushing fixes to a PR, **don't** post `@codex review` or `@claude please re-review` comments.

**Why:** Codex has limited quota — extra rounds burn it for no gain. Claude reviews are automatic on push (`synchronize` trigger), so re-pinging is redundant.

When working a PR review fix-loop: push the fix → thread-reply to each finding → stop. Don't add a "please re-review" comment. The Claude bot picks up the new commit on its own; if Codex doesn't, that's intentional.

**Related:** [`.github/workflows/claude-review.yml`](../../.github/workflows/claude-review.yml) — auto-review fires on `synchronize`; the prompt fetches existing thread state so re-flags don't accumulate.

## 5. React 👍 / 👎 on every Codex finding

Codex closes each finding with "Useful? React with 👍 / 👎." That reaction is its feedback signal — it tunes what Codex flags on later PRs. A thread reply alone doesn't reach it.

```bash
gh api -X POST repos/<owner>/<repo>/pulls/comments/<comment_id>/reactions -f content='+1'   # or '-1'
```

Note the path: `/pulls/comments/<id>/reactions`, **not** `/pulls/<N>/comments/<id>/reactions` (that's the replies path).

The reaction tracks the triage verdict, one per finding, in the same step as the reply:

| Verdict | Reaction |
|---|---|
| VALID + fix | 👍 (`+1`) |
| INVALID — bot is factually wrong | 👎 (`-1`) |
| WONTFIX — correct observation, deliberately not changing | 👍 (`+1`) |

WONTFIX still gets 👍: the find was accurate, the decision was ours. Downvote only when Codex's claim about the code is false — otherwise the signal teaches it to stop reporting things that were right.

## 6. Top-level review comments have no thread — close the loop with a follow-up comment

When findings were posted as a **top-level PR comment** (not inline threads) — including your own `/code-review` output — and you then fix them, post a follow-up comment recording what was fixed. There's no thread to resolve, so the reply/resolve habits above never fire, and the findings sit there reading as open work.

**Why:** on one PR, a 3-issue top-level review was posted, all 3 were fixed and pushed, and the fix was reported in chat — but nothing was posted on the PR itself. Peter: "did you comment on the pr that you fixed those three things?" Anyone reading the PR would have seen three unaddressed findings.

**How to apply:** the follow-up comment states what was fixed and the sha, and — importantly — what was deliberately *not* changed and why, including any finding that turned out to be a false positive on closer inspection. Fixing review findings isn't done at "pushed."
