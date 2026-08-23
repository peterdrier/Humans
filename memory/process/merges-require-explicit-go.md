---
name: merges-require-explicit-go
description: Never merge a PR without Peter's explicit, per-instance go. With that go, merging is fine — it is not a destructive action.
---

Never merge a pull request — `gh pr merge`, `--admin`, `--auto`, the API — unless Peter has explicitly said to merge that PR. Merging is not destructive in the sense of [[no-destructive-actions-without-approval]] (nothing is lost; production data is untouched), so once the go is given, just do it.

**Why:** a prior incident merged PRs with `--admin` after a message that only *sounded* like blanket authorization ("I want to merge them all"), which also auto-closed a stacked PR when its base branch was deleted. The failure was inferring the go, not the merge itself.

**How to apply:**
- Default deliverable around merging is information — ordering, risk notes, "this one's safe" — then stop.
- "Merge it" for a named PR (or an explicit list) is the go. A vague wish, an idle agent, or a green check is not.
- Surface anything he should know first (stacked PRs, migrations, deploy effects), then merge as instructed.
- Approval for one PR doesn't carry to the next.
