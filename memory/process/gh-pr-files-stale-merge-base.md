---
name: gh-pr-files-stale-merge-base
description: A PR showing a huge phantom diff (hundreds of files it never touched, usually marked "removed") is a stale GitHub merge base — merge current main to clear it, don't rebase or panic.
---

GitHub's `pulls/{n}/files` (and the PR's `changedFiles`/`additions`/`deletions`) can serve a **stale diff computed against an old merge base**, showing hundreds of files the PR never touched — typically as `removed`. Seen on a docs-only PR: the endpoint reported 1167 files and −657824 lines, listing every migration `Designer.cs` as deleted, when reality was 11 files and −177 lines.

**How to tell it's phantom** — three cheap checks that disagree with the PR endpoint:
- `gh api repos/OWNER/REPO/compare/main...BRANCH --jq '.files|length'` → the real count
- `git diff --stat $(git merge-base origin/main origin/BRANCH) origin/BRANCH`
- `git ls-tree -r --name-only <ref> -- <allegedly-deleted-path>/ | wc -l` on main, the branch, and the merge base — equal counts mean nothing was deleted

**Fix:** `git merge origin/main` on the branch and push. The new `synchronize` event recomputes the merge base and the diff collapses to the truth. A rebase + force-push also works but isn't needed — don't spend a force-push (or the per-instance approval it requires) on this.

**Knock-on damage to expect:** path-matching workflows read the *stale* file list, so a PR can get labeled (e.g. `db`) and trigger a migration gate with no migration in it. The label clears itself on the next correct run — never hand-remove it, that's treating the symptom instead of the cause.

Also confirm "no open PRs" with a per-PR `gh api .../pulls/N` before acting on an empty `gh pr list` — that call can time out and return empty transiently.
