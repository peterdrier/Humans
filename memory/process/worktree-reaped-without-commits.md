---
name: worktree-reaped-without-commits
description: An isolation:worktree agent that goes idle before its first commit has its worktree deleted by the harness; resuming it lands in the shared main checkout instead.
---

`isolation: "worktree"` worktrees are auto-removed when the agent stops **if the worktree is unchanged** — i.e. it has no commits. The branch survives (pointing at `origin/main`), but the directory is gone from disk and from `git worktree list`.

Consequence: an agent that goes idle mid-work before checkpointing loses its worktree. Resuming it then puts it in the **shared main checkout** — it looks to the agent like its workspace vanished, or like the resume message is injected content. Observed once mid-sprint; the agent correctly halted instead of pivoting, and the main checkout stayed undamaged.

**How to apply:** in every worker prompt, make the first action after `git checkout -b` an empty commit — `git commit --allow-empty -m "wip: start #<N>"` — so the worktree is never in a commitless state. Reinforce checkpoint-early-and-often for the same reason. If a worktree is already reaped, spawn a fresh agent with the salvaged findings rather than resuming the old one into the main checkout.

Related: [[no-rm-rf]], [[agent-isolation-worktree-base]].
