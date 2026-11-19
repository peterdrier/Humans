---
name: model-tiering
description: Which model/effort a subagent gets is in the pd plugin's orch skill (`/pd:orch`, routing.md); this atom adds the Haiku context-vacuum-reader technique and dispatch timing/constraints.
---

Which model and effort a subagent gets is decided by the orch skill's `routing.md` (pd plugin,
`/pd:orch`). This atom adds what that table doesn't.

**Context-vacuum readers.** A Haiku subagent reads the bulky thing and returns a tiny summary, so the
bulk never enters the main context. Best targets: build logs, test logs, large diffs, the public shape
of a big file, long application-log scans.

- Dictate the output format exactly ("each error as `<path>:<line> CS#### <message>`; no preamble, no
  fix suggestions; `NO_ERRORS` if none").
- Spot-check one finding against the source before editing on the strength of a summary.
- Not worth it for small reads; do those inline.
- Haiku parses, it doesn't decide. Architectural judgment goes to a stronger model.

**Dispatch constraints:**
- Fan out as wide as the work is independent; there is no parallel cap. Dependent steps stay serial.
- Decide the design first, then dispatch. A subagent can't hold a design dialogue.
- Brief it with everything it needs (paths, exact methods, the move map); it can't see the conversation.
- Its summary describes intent. Verify with `git diff --stat` and a spot check before trusting "done".
- Dispatch right after the design is settled, while the brief is crisp, not five build-fix loops in.

**Why:** most of a long refactor is mechanical execution; running all of it on the top model costs
several times more for the same result.
