---
name: comments-stay-short
description: Code comments and doc blocks get 1-3 lines stating what's true now and why — never a history of the decision.
---

Comments state what is true now and why, in one to three lines. No decision history, no "used to be X, then Y moved it, which cost Z", no chain of issue refs, no restating what the code below already says.

If the rationale genuinely needs a paragraph, it belongs in the issue or a memory atom — link it, don't inline it.

**Why:** the comment gets read to understand a three-second problem. A long history buried in a doc block is noise to scan past every time.

**How to apply:** same for XML doc blocks and `because:` strings in test assertions. When editing an existing over-long comment, shorten it rather than adding to it.

Related: [[name-analyzers-not-numbers]].
