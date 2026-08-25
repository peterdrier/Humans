---
name: always-use-worktree
description: HARD RULE. All branch work, including main itself, happens in a worktree under .worktrees/<name>. The main H:\source\Humans checkout is read-only — never edit, stage, commit, reset, or checkout there. Create the worktree first, before any exploration, and scope every Glob/Grep to it.
---

**HARD RULE.** ALL branch work — including work on `main` itself — happens in a git worktree under `.worktrees/<name>`. The main checkout is read-only: never edit files, stage, commit, reset, or checkout there.

**Why:** The main checkout is shared with other concurrent agents and tooling. At any moment another process may have uncommitted changes on `main` there — resetting or switching branches destroys that work, and an uncommitted edit can be wiped if something else switches branches mid-edit. Peter: "you ARE NEVER ALLOWED TO WORK IN MAIN" — applies to the main checkout for any branch, including main.

**How to apply:**
- New work: `git worktree add -b <slug> .worktrees/<slug> origin/main`, commit on that branch, push it, open a PR ([[no-direct-to-main]] — the standalone `memory/**` direct-main case is defined there, and even that is pushed from the worktree, never from the main checkout).
- Existing branch: `git worktree add .worktrees/<slug> <branch>`.
- `cd` to that worktree once and stay there. Never `git checkout`, `git reset`, `git stash`, or delete/move tracked files in the main checkout.
- If the main checkout has uncommitted changes on arrival, assume they belong to another agent/process and leave them alone.
- When a skill finds the current branch differs from a target PR's head branch, the answer is always "use a worktree" — check `git worktree list` for an existing one first, or create one. Don't present it as a multi-option question.
- Sole exception: the post-production-merge fork reset in [[after-prod-merge-reset]], run exactly as that atom says and nothing more.

## Scope every search to the worktree

Create the feature worktree **first**, before any code exploration, then do all reading/searching/editing inside it. **Never run `Glob`/`Grep` against the repo root.** Full checkouts live under it in multiple places — `.worktrees/` (feature worktrees) and any nested agent worktree directories — and the count moves constantly as parallel sessions create and clean them. Each is a complete copy of the codebase at a different commit, so an unscoped search returns duplicate hits per file from mutually inconsistent revisions — that's what poisons the search, at any count.

`Glob`/`Grep` ignore the Bash `cd` — they default to the repo root — so you MUST pass an explicit `path` every time, scoped to `<worktree>/src` and `<worktree>/tests` (subtrees that hold no nested worktrees).

**Why:** on one issue, exploring the main checkout for many turns before making the worktree, then globbing the polluted root repeatedly, burned tens of thousands of tokens on worktree-noise alone.

**How to apply:** step 1 of any feature task — after the issue-authorization preflight in [[issue-fetch-protocol]] for issue-driven work — is `git worktree add -b <name> .worktrees/<name> origin/main`. From then on, every Glob/Grep carries an explicit `path` under that worktree; never omit `path` at the repo root.

Related: [[worktrees-off-origin-main]], [[pr-fix-switches-to-pr-branch]].
