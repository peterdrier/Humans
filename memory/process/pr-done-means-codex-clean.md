---
name: "Done" means Codex-clean, not just pushed
description: A PR isn't done until Codex returns no findings. Pushed + green build + green tests is mid-state. Don't mark tasks `completed` until codex-clean.
---

"Done" for a PR means Codex has signed off with no open findings. Pushed + clean build + green tests is **mid-state**, not done.

**Why:** A pushed branch can still have real runtime bugs that the build won't catch (e.g., the circular-DI P0 Codex caught on PR #267 after the rebase build went green). Shipping based on "build green" alone has burned us before.

**How to apply:**

- Status language: "pushed", "build green", "CI green", "awaiting Codex" — never "done" until Codex returns clean (or any new findings are themselves resolved).
- Task state: keep tasks `in_progress` through the full push → CI → codex-triage → codex-clean loop. Mark `completed` only at codex-clean.
- Even if the user has given a looser DoD ("just build + push") for a specific swarm or batch, treat Codex-clean as the real bar unless they explicitly waive it again in the current turn.
- Applies to single-PR work and multi-PR swarms. Don't let a 22-PR pipeline shortcut the per-PR convergence check.
- **Do NOT ping `@codex review` to re-trigger** — see [`pr-no-ping-reviewers`](pr-no-ping-reviewers.md). Wait for the automatic review on push.
