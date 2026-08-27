---
name: review-round-budget
description: Unattended, a PR gets at most five review-round commits — commits pushed in response to an automated review finding or a CI failure, counted from the point the PR's own work is functionally complete. Peter's instructions, the PR's own deliverable and mechanical commits are not rounds and do not raise the cap. On any CI/review wake for a PR you opened, read .claude/skills/steward/SKILL.md FIRST and count before reading any finding. At the ceiling — one summary comment, unsubscribe, stop. Raising the ceiling needs Peter.
---

Unattended review rounds on a PR are capped at **five review-round commits**. Before acting on
any CI failure or review finding on a PR you opened, read
[`.claude/skills/steward/SKILL.md`](../../.claude/skills/steward/SKILL.md) and run its
count first.

**What a round is:** a commit pushed *in response to an automated review finding (Codex,
the Claude bot, Gemini) or a CI failure*, counted from the point the PR's own work is
functionally complete. Nothing else is a round:

- commits answering Peter's own instructions — a section-doctor run applying his
  Needs-Peter answers is the deliverable, not a round
- commits finishing what the PR was opened to deliver
- mechanical commits: rebases, merges of the base branch, conflict resolution

The reverse holds too: a maintainer instruction does not reset the count or raise the
ceiling. Five bot rounds stay five, whatever else lands on the PR.

**Why:** each review event arrives as a fresh wake, and by then the context that would
have said "this is round seventeen" has been compacted away — so every round looks like
round one, and "stop when it stops converging" gets re-derived and rationalised past on
every firing. A count derived from the PR is the only thing in the loop that remembers.
Bots have no memory between rounds either; they are generators, not reviewers working
toward done — deciding the review is over is your job, and past ~5 rounds the next commit
is likelier to open a leak than close one. That reasoning is about bot loops, which is why
finishing the PR, or doing what Peter asked, spends nothing.

**How to apply:**

The count answers one question — *how many times has this PR already churned on automated
review?* Everything below serves that. Where a case isn't listed, answer that question and
act on the answer; don't hunt for a rule that names it.

- Every review-round commit carries a `Review-round: <n>` trailer, `<n>` being its own
  number. Count spent = those trailers on the PR (command in the skill). A commit that is
  not a round must not carry one.
- The trailer is the memory, not the definition — a record you keep for a future you who
  has lost the context, not an enforcement mechanism. It is self-declared on purpose
  (Peter, 2026-08-26: self-declaration has worked in practice). A round commit that
  predates this rule or lost its trailer to a rebase still counts; where the trailers and
  the PR's review history disagree, the history wins.
- At 4 spent: last commit — read every open finding, spend it on the most serious.
- At 5+: no triage, no patch. One comment (open items, what you'd do, what needs
  deciding), unsubscribe from PR activity, drop any check-in schedule, stop.
- One round = one commit; batch the round's fixes.
- Raising the ceiling, or pushing past it, needs Peter to say so in his own words.

**Related:** [[review-finding-triage]] · [[pr-review-feedback-handling]]
