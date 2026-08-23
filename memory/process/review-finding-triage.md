---
name: Review-finding triage — judge before fixing, close out every finding
description: Any review finding (Codex, Claude bot, Gemini, human) is a hypothesis, not a work item. Verify it against the code and judge whether it deserves a fix before changing anything; every finding ends with a visible disposition (fixed / not fixing / issue opened).
---

Review findings are hypotheses, not a work list. Verify each one against the actual code and judge whether it deserves a fix **before** changing anything — never work through reviewer output as a checklist.

**Why:** Blind-fixing every finding burns tokens and ships changes nobody wanted. Codex in particular is regularly wrong about the code, short-sighted about the design, or flags things unrelated to the PR. The judgment step was getting skipped because fixing was the default.

**How to apply:**

1. **Verify first.** Read the code the finding points at. A claim that doesn't reproduce is declined as factually wrong, not fixed.
2. **Judge.** Fix what's real and in scope: bugs in code this PR touched, [`code-review-rules.md`](../../docs/architecture/code-review-rules.md) / hard-rules violations, drift from the linked issue's spec. Decline what isn't: unrelated to this PR (if real, open an issue or a debt-ledger entry instead) · speculative / never-observed hypotheticals · contradicts project posture (scale/pagination advice, concurrency tokens, emailless users, anonymous-endpoint perf guards — see the false-positive atoms in `INDEX.md`) · pure style preference.
3. **Rounds are judgment too.** Some PRs justify five fix rounds, some one. Continue while re-reviews surface real new problems; stop when they don't.
4. **Close out every finding — none left dangling.** Each gets a reply in its own thread stating the disposition: "fixed in `<sha>`", "not fixing — `<reason>`", "opened `<owner>#N`". Mechanics (thread replies, resolves, 👍/👎 reactions): [`pr-review-feedback-handling`](pr-review-feedback-handling.md).

When fixing is delegated to a subagent, the orchestrator does the triage and the subagent's prompt contains only the findings judged worth fixing — never raw reviewer output.
