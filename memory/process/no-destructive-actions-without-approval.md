---
name: no-destructive-actions-without-approval
description: HARD RULE. Never take a destructive/irreversible action (history rewrite, force-push, branch/file deletion, DB write, runtime edit) without Peter's explicit per-instance approval.
---

Never take a destructive or irreversible action without Peter's explicit instruction for that instance
and that target.

- **Git history:** manual `merge --squash`, rebasing published history, `reset --hard`, force-push,
  `branch -D`, amending pushed commits, dropping or fixing-up commits.
- **Database:** any write outside an EF migration, on any environment
  ([`no-manual-db-writes`](no-manual-db-writes.md)).
- **Files:** deleting files nobody asked to delete, overwriting uncommitted work, clearing caches, locks
  or state files to "unstick" a process.
- **Runtime state:** hand-editing deployed config, env vars, secrets, feature flags.
- **External services:** deleting remote branches, deleting or locking issues, revoking tokens or
  grants, deleting cloud resources.

**Standing carve-outs:**
- Squash-merge of a feature PR into `peterdrier/Humans` main, and the production PR's merge into
  `nobodies-collective/Humans`. Neither extends to manual `git` history commands.
- The post-production-merge reset in [`after-prod-merge-reset`](after-prod-merge-reset.md).
- Restarting a cloud session's own designated branch after its PR merged (only already-merged history).

**Why:** told "this PR has acquired a lot of cruft, close it and make a new one", an agent squashed
about fifty reviewable commits into one. Peter: *"you do not ever flatten a commit history without my
explicit instructions."* and *"You need to learn not to make destructive actions in ANY WAY SHAPE OR
FORM without my explicit instructions. DB, git history, it doesn't fucking matter."*

**How to apply:**
1. Before anything that *removes* information (commits, rows, files, branches, runtime state), ask,
   quoting the operation and the target.
2. "Cruft", "messy", "stale", "unused", "old" describe a state, not an action. Confirm the action.
3. Approval is per instance and per target; an earlier approval covers nothing else.
4. When a workflow seems to need destruction, present the non-destructive alternative first (a new
   branch that adds the work, leaving the old branch and PR alone).

**Related:** [`no-hand-edited-migrations`](../architecture/no-hand-edited-migrations.md) ·
[`destructive-drops-need-approval`](../architecture/destructive-drops-need-approval.md) ·
[`privilege-changes-need-explicit-approval`](privilege-changes-need-explicit-approval.md)
