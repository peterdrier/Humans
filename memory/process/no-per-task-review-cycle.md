---
name: no-per-task-review-cycle
description: When executing a plan via subagent-driven-development, skip per-task spec/quality-reviewer dispatches — implement, mark complete, move on; review the whole body of work once at the end.
---

When executing a plan via `superpowers:subagent-driven-development`, don't dispatch a per-task spec-reviewer or code-quality-reviewer subagent for every task. Dispatch the implementer, mark complete, move on. Review the entire body of work in one pass at the end (final code review + the PR's own Codex/Claude review).

**Why:** per-task two-stage review burns tokens roughly 3x per task for marginal gain. On a large plan that's dozens of extra subagent dispatches when the PR is going to get a full review anyway. Peter: "stop reviewing every time you tied your shoes like you're a 2 year old. We'll review en masse when the whole thing is complete."

**How to apply:**
- Dispatch an implementer subagent per task — that's it.
- The implementer's own self-review (built into its prompt) is the only review during execution.
- Mark a task complete on green build/tests + commit; move to the next task.
- One final code review at the end of all tasks, or rely on PR-time bot review.
- Applies generally to subagent-driven-development executions, not just a single plan.
