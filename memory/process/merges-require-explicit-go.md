---
name: merges-require-explicit-go
description: Never merge a PR without Peter's explicit, per-instance go — and never OFFER to merge; the offer invites the ambiguous reply. With the go, merging is fine — it is not a destructive action.
---

Never merge a pull request — `gh pr merge`, `--admin`, `--auto`, the API — unless Peter has explicitly said to merge that PR. Merging is not destructive in the sense of [[no-destructive-actions-without-approval]] (nothing is lost; production data is untouched), so once the go is given, just do it.

**Never offer to merge.** Don't end a status report with "want me to merge?" — the offer invites an ambiguous reply that gets misread as consent. Report the PR's state and stop; Peter says "merge it" when he wants it merged.

**Why:** a prior incident merged PRs with `--admin` after a message that only *sounded* like blanket authorization ("I want to merge them all"), which also auto-closed a stacked PR when its base branch was deleted. The failure was inferring the go, not the merge itself. A second incident (2026-08-26): an agent asked "Want me to merge #1515 now?", read "ok fine, my bad need to finish that up" as a yes, and merged. Peter: "flat rule is you never merge. I have to explicitly ask for it, you should never offer."

**How to apply:**
- Default deliverable around merging is information — ordering, risk notes, "this one's safe" — then stop. No merge offers.
- "Merge it" for a named PR (or an explicit list) is the go. A vague wish, an acknowledgment ("ok fine"), an idle agent, or a green check is not.
- Surface anything he should know first (stacked PRs, migrations, deploy effects), then merge as instructed.
- Approval for one PR doesn't carry to the next.
