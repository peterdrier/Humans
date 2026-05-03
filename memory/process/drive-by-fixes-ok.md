---
name: Drive-by fixes are fine when a human reviews the PR
description: When fixing a thing in a PR, an unrelated small fix (stale doc, dead field, obvious typo) discovered alongside is OK to land in the same PR. Don't flag as scope creep, don't auto-revert.
---

When applying review fixes to a PR, an incidental small fix discovered alongside the in-scope work is fine to include in the same commit/PR. Don't reflexively revert it as "scope creep" and don't open a separate PR for it.

**Why:** Peter ships review through PRs that he reads end-to-end before merging. The reason for tight-scope discipline elsewhere — auto-merging bots, distracted reviewers, hard-to-revert blast radius — doesn't apply to this project's flow. A drive-by doc fix or dead-code delete in the same PR saves a round-trip and a human review pass; the scope-mixing cost is paid by Peter, who's already reading the diff. The cost/benefit lands on "include it."

**How to apply:**

- Mid-PR, if a fix subagent discovers an unrelated stale row in a doc table, an unused field, an obvious one-line bug, etc. — let it stay in the commit. Note it explicitly in the commit message ("also fixes …") so the human reviewer can spot it.
- DO surface drive-bys in the PR description / review report so they're visible. The agreement is "human reviews everything", not "anything goes silently."
- DO NOT smuggle in larger refactors (renames across files, new abstractions, behavior changes). Drive-by means small + obvious + low-risk. If it needs design judgment, it's its own PR.
- DO NOT silently revert a subagent's drive-by edit when reviewing its work — surface it to Peter and let him decide. Default answer when in doubt is "keep it."

**Examples of OK drive-bys:** stale §8 ownership table row, dead `_dbContext` field, missing `nameof()` on a magic string, typo in a comment.

**Examples of NOT OK drive-bys:** rename a method used in 30 files, introduce a new helper class, change auth semantics, move a file across layers.
