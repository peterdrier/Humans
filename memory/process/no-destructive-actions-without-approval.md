---
name: No destructive or irreversible actions without explicit per-instance approval
description: HARD RULE. Never take a destructive/irreversible action — git history rewrites, branch deletions, force-pushes, DB writes outside migrations, file deletions, runtime state edits — without Peter's explicit, per-instance instruction. "Cleanup" implications, "cruft" descriptions, and prior approvals on similar actions don't carry over. The only standing flatten is the squash-merge GitHub button on a fork PR landing on `peterdrier/main`.
---

Never take a destructive or irreversible action without Peter's explicit, per-instance instruction. The class includes (non-exhaustive):

- **Git history**: `git merge --squash`, `git rebase` (interactive or otherwise) that rewrites shared/published history, `git reset --hard`, `git push --force` / `--force-with-lease`, `git branch -D`, `git commit --amend` on pushed commits, `git filter-branch`/`filter-repo`, `git reflog expire`, dropping commits, fixing-up commits.
- **Database**: any `INSERT`/`UPDATE`/`DELETE` outside an EF migration; any `DROP TABLE`, `TRUNCATE`, `DELETE FROM __EFMigrationsHistory`; any direct edit to runtime DB state (admin DB, QA DB, preview DB, prod DB — all of them).
- **Files**: deleting files the user didn't ask to delete; overwriting uncommitted work; clearing caches/locks/state files to "unstick" a process.
- **Runtime state**: hand-editing deployed config, runtime DI registrations, container env vars, secrets, runtime feature flags.
- **External services**: deleting/closing GitHub branches, force-pushing to remote branches, deleting/locking issues, revoking OAuth tokens, removing IAM grants, deleting cloud resources.

**The ONLY standing exception is the squash-merge of a feature PR into `peterdrier/main`** — that's the existing two-remote convention (`CLAUDE.md` § Git Workflow — Two-Remote). Every other flatten/squash/rebase/destroy needs per-instance approval, even if a similar action was approved earlier in the conversation or is hinted at by a description like "this PR has a lot of cruft".

**Why:** Discovered 2026-05-10 on PR #421 → #472 replacement. Peter said "this PR has acquired a lot of cruft, close it and make a new one." I read that as authorization to `git merge --squash` ~50 logical feature commits into one, destroying the per-feature-aspect commit ladder that made the work reviewable. Peter's correction: *"you do not ever flatten a commit history without my explicit instructions. We ONLY ever do that when merging a final pr into origin/main."* Followed by *"You need to learn not to make destructive actions in ANY WAY SHAPE OR FORM without my explicit instructions. DB, git history, it doesn't fucking matter. Stop destroying things."* The cost of pausing to confirm is low; the cost of an unwanted destructive action is unrecoverable work or lost review structure.

**How to apply:**

1. Before any operation that *removes* information from a system — commits, rows, files, tags, branches, runtime state — ask first. Quote the operation and the target. Do not phrase the question as "should I do X or Y" if both are destructive; offer "Y or stop and wait."
2. "Cruft," "messy," "stale," "unused," "old," "redundant" in user description ≠ authorization to delete or rewrite. They describe the *state*, not the *action*. Confirm the action separately.
3. Authorization is per-instance and per-target. Approval to squash one branch is not approval to squash another. Approval to delete one file is not approval to delete adjacent files.
4. When considering a workflow that requires destruction (re-creating a PR with a clean branch, regenerating a migration, etc.), present the **non-destructive alternative first** (e.g., open the new PR from a branch that *additively* incorporates the work; leave the old branch and PR untouched) and ask which path to take.
5. The standing carve-outs are:
   - **Squash-merge of a fork PR into `peterdrier/main`** (the existing convention; performed by the GitHub UI button, not by manual `git` commands).
   - **Rebase-merge of a production PR into `nobodies-collective/main`** (also a UI action; per `CLAUDE.md`).
   - Neither extends to manual `git rebase`/`merge --squash`/`push --force` from Claude's terminal.
6. Memory atoms can be added without per-instance approval — they have no runtime effect, no destructive component, and the carve-out is documented in [`no-direct-to-main`](no-direct-to-main.md).

**Related:**
- [`no-direct-to-main`](no-direct-to-main.md) — fork PR workflow.
- [`after-prod-merge-reset`](after-prod-merge-reset.md) — the one approved post-merge `git reset --hard` (against `upstream/main` only, after a production merge).
- [`migration-regen-after-rebase`](../architecture/migration-regen-after-rebase.md) — EF migration regen needs explicit approval before the `migrations remove` + rebase dance.
- [`no-hand-edited-migrations`](../architecture/no-hand-edited-migrations.md) — narrow scope: migration files specifically.
- [`privilege-changes-need-explicit-approval`](privilege-changes-need-explicit-approval.md) — narrow scope: capability grants specifically.
