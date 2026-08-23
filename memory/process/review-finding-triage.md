---
name: Review-finding triage — verdict before fix
description: Any review finding (Codex, Claude bot, Gemini, human) is a hypothesis, not a work item. Triage every finding to Fix / Decline / Needs-Peter before changing any code; fix only Fix verdicts; one fix cycle per PR.
---

Review findings are hypotheses, not a work list. Before touching any code, verify each finding against the actual code and assign a verdict — **Fix**, **Decline**, or **Needs-Peter** — then fix only the Fix column.

**Why:** Blind-fixing every finding burns tokens and ships changes nobody wanted. Codex in particular is regularly wrong about the code, short-sighted about the design, or flags things unrelated to the PR. The judgment step was getting skipped because fixing was the default; this atom makes the verdict the default and the fix conditional.

**How to apply:**

1. **Verify first.** Read the code the finding points at. A claim that doesn't reproduce is a Decline (bot factually wrong), not a fix.
2. **Verdicts:**
   - **Fix** — a real bug in lines this PR touched · a [`code-review-rules.md`](../../docs/architecture/code-review-rules.md) or hard-rules violation · drift from the linked issue's spec.
   - **Decline** (the default) — unrelated to this PR (if real, record it in the debt ledger or an issue instead) · speculative / never-observed hypothetical · contradicts project posture (scale/pagination advice, concurrency tokens, emailless users, anonymous-endpoint perf guards — see the false-positive atoms in `INDEX.md`) · pure style preference.
   - **Needs-Peter** — plausible, but the call is his (public surface, behavior change, scope growth). List it with a recommendation; do not fix.
3. **One fix cycle per PR.** After the fix push, the re-review only earns another round if it flags a new real bug in the fix itself.
4. **Close out per** [`pr-review-feedback-handling`](pr-review-feedback-handling.md) — per-thread replies, resolves, 👍/👎 reactions.

When fixing is delegated to a subagent, the orchestrator does the triage and the subagent's prompt contains **only the Fix verdicts** — never raw reviewer output.
