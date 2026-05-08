---
name: Resolve declined PR threads when Peter has authorized the deviation
description: In `/pr-fix`, "decline because Peter authorized" → resolve the thread; reserve leave-open behavior for technically-wrong findings.
---

When `/pr-fix` triages a review-bot finding as "decline because Peter authorized" (e.g., budget bumps acknowledged in the PR body, or a "leave at N" answer to `AskUserQuestion`), **resolve the thread** in the same step as posting the reply.

**Why:** The pr-fix skill's default "leave INVALID open so reviewers can push back" rule is for cases where Claude judged the bot wrong. When Peter authorized the deviation, the matter is settled — leaving the thread open is just clutter and a re-review trigger. Discovered on PR #448 when the bot flagged the same authorized budget bump twice and Peter had to ask for the second thread to be manually resolved.

**How to apply:**

- "Not changing — Peter authorized X" reply → resolve the thread.
- "Not changing — bot is technically wrong" reply → leave open (default skill behavior).
- The decision rule: did Peter explicitly OK this deviation? Resolve. Otherwise leave open.
