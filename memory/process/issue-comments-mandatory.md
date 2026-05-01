---
name: Always read the entire GitHub issue including comments
description: HARD RULE (hook-enforced). Before implementing ANY issue or PR, fetch with comments. The OP is often not Peter; his comments often flip the intent.
---

Before implementing any GitHub issue, **always fetch it with comments included** — never body-only. The original poster is often not Peter, and Peter's comments in the thread frequently redirect, narrow, or flip the OP's proposed fix.

**Why:** Implementing from the body alone has repeatedly produced wrong features. Concrete instance 2026-04-24: issue `nobodies-collective/Humans#578` OP proposed adding an `isConfirmedForYear` flag. Peter's comment said the `/year` URL filter should natively exclude camps not in that edition (i.e. fix the filter, not add a flag). Body-only implementation shipped PR #310 with the flag — had to be closed as a wrong feature.

A pre-commit/pre-tool hook (`require-gh-comments.sh`) blocks `gh issue view` / `gh pr view` calls that don't include comments.

**How to apply:**

- For `gh issue view`, always pass `--json title,body,author,comments` (or `--comments`/`-c` for plain mode). Never body-only.
- When dispatching batch-worker subagents from `/execute-sprint`, fetch comments in the orchestrator's pre-flight spec pull AND include the comment text **verbatim** in the subagent prompt — subagents don't have `gh` context or the instinct to re-fetch.
- Treat comments from Peter (`peterdrier`) as authoritative when they conflict with the OP body; the issue body captures the OP's perspective, comments capture the decision.
- If comments materially change the spec, flag it in the PR body ("per `nobodies-collective/Humans#NNN` comment from @peterdrier on DATE, scope is …") so reviewers can trace the intent.
- Applies to `gh pr view` too: review comments and discussion often contain the real ask.

**Related:** [`issue-no-non-peter-without-approval`](issue-no-non-peter-without-approval.md).
