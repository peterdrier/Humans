---
name: review-round-budget
description: Unattended, a PR gets at most five post-PR commits. On any CI or review wake for a PR you opened, read .claude/skills/steward/SKILL.md FIRST — count the spent commits from the PR before reading any finding. At the ceiling — one summary comment, unsubscribe, stop. Raising the ceiling needs Peter.
---

Unattended review rounds on a PR are capped at **five post-PR commits**. Before acting on
any CI failure or review finding on a PR you opened, read
[`.claude/skills/steward/SKILL.md`](../../.claude/skills/steward/SKILL.md) and run its
count first.

**Why:** each review event arrives as a fresh wake, and by then the context that would
have said "this is round seventeen" has been compacted away — so every round looks like
round one, and "stop when it stops converging" gets re-derived and rationalised past on
every firing. A count derived from the PR is the only thing in the loop that remembers.
Bots have no memory between rounds either; they are generators, not reviewers working
toward done — deciding the review is over is your job, and past ~5 rounds the next commit
is likelier to open a leak than close one.

**How to apply:**

- Count spent = commits on the PR after `createdAt` (command in the skill).
- At 4 spent: last commit — read every open finding, spend it on the most serious.
- At 5+: no triage, no patch. One comment (open items, what you'd do, what needs
  deciding), unsubscribe from PR activity, drop any check-in schedule, stop.
- One round = one commit; batch the round's fixes.
- Raising the ceiling, or pushing past it, needs Peter to say so in his own words.

**Related:** [[review-finding-triage]] · [[pr-review-feedback-handling]]
