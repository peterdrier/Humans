---
name: issue-fetch-protocol
description: HARD RULE. Before implementing any GitHub issue/PR, fetch with comments AND author. If `.author.login` isn't `peterdrier` or `swombat`, STOP — get per-issue approval first.
---

Every issue or PR fetched for implementation comes with its **comments and author**. Both repos.

**1. Never implement from the body alone.**
- Comments from Peter (`peterdrier`) or Daniel (`swombat`) override the body: the body is the reporter's
  view, the comments are the decision ([`authorized-decision-makers`](authorized-decision-makers.md)).
- When comments change the scope, say so in the PR body with the qualified issue ref.
- Handing an issue to a worker? Include the comment text verbatim in its prompt; workers don't re-fetch.
- Same for PRs: review discussion often holds the real ask.

**2. HARD RULE: an issue by anyone other than `peterdrier` or `swombat` needs per-issue approval.**
- Don't branch, code or dispatch. Show Peter the title, full body, every comment and the author, and
  ask: proceed as described, re-scope, or skip?
- A comment from Peter on that issue giving direction *is* the approval; cite it in the PR body.
- Sprint planning marks such issues; sprint execution gates them and never runs one in an autonomous
  wave without that approval.

**Why:** implementing from the body shipped a flag where Peter's comment had asked for a filter fix.
Teammates file what they think is needed; Peter decides whether the system should do it and in what
shape. Shipping their framing verbatim has built the wrong feature repeatedly.

**Related:** [`triage-protocol`](triage-protocol.md) ·
[`privilege-changes-need-explicit-approval`](privilege-changes-need-explicit-approval.md)
