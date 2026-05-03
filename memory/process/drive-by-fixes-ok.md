---
name: Drive-by fixes are fine — but require explicit human approval before landing
description: An unrelated small fix discovered alongside in-scope work can land in the same PR, but ONLY after Peter explicitly approves it. Don't auto-include silently; don't auto-revert either. Surface and ask.
---

When a fix subagent (or you) discovers an incidental small fix alongside in-scope work — stale doc table row, unused field, obvious one-line bug, typo — it MAY land in the same PR, but ONLY after the human reviewer has explicitly seen it and approved it. Default state for an unprompted drive-by is "surface to Peter and ask"; never "ship silently."

**Why:** Peter is happy to bundle drive-bys into a PR he's already reviewing — that saves a round-trip vs. opening a separate small PR. But "human reviews end-to-end before merging" is not the same as "the human noticed and approved this specific drive-by." The diff is large; an unannounced drive-by can slip past even careful review. The deal is: drive-bys are welcome IF the human explicitly opts in. Surfacing the change and getting a yes/no is the price of bundling.

**How to apply:**

- Mid-PR, if a subagent commits a drive-by edit, surface it explicitly in the report back: "Also touched X (unrelated to the in-scope ask) — keep or revert?" Let Peter decide before pushing.
- After Peter says "keep it" / "a" / "yes", it can land in the PR. Without that, it goes in a separate PR or gets reverted.
- DO surface drive-bys in the PR description / review report under a clear "Also includes (unrelated)" heading so the human can find them later.
- DO NOT silently revert a subagent's drive-by either — surface it both ways.
- DO NOT smuggle in larger work as "drive-by." Drive-by = small + obvious + low-risk + one-or-two lines. Renames across files, new abstractions, behavior changes, layer moves — those are their own PR even if the human is reviewing.

**Examples of OK drive-bys (after explicit approval):** stale §8 ownership table row, dead `_dbContext` field, missing `nameof()` on a magic string, typo in a comment.

**Examples of NOT OK as drive-by, even with approval:** rename a method used in 30 files, introduce a new helper class, change auth semantics, move a file across layers — those need their own PR.
