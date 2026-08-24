---
name: worktree-first-scoped-search
description: Create the feature worktree as step 1, before any exploration, then scope every Glob/Grep to it — never search the repo root, which holds many parallel worktree checkouts at different revisions.
---

Create the feature worktree **first**, before any code exploration, then do all reading/searching/editing inside it. **Never run `Glob`/`Grep` against the repo root.** Full checkouts live under it in multiple places — `.worktrees/` (feature worktrees) and any nested agent worktree directories — and the count moves constantly as parallel sessions create and clean them. Each is a complete copy of the codebase at a different commit, so an unscoped search returns duplicate hits per file from mutually inconsistent revisions — that's what poisons the search, at any count.

`Glob`/`Grep` ignore the Bash `cd` — they default to the repo root — so you MUST pass an explicit `path` every time, scoped to `<worktree>/src` and `<worktree>/tests` (subtrees that hold no nested worktrees).

**Why:** on one issue, exploring the main checkout for many turns before making the worktree, then globbing the polluted root repeatedly, burned tens of thousands of tokens on worktree-noise alone.

**How to apply:** step 1 of any feature task — after the issue-authorization preflight in [[issue-fetch-protocol]] for issue-driven work — is `git worktree add -b <name> .worktrees/<name> origin/main`. From then on, every Glob/Grep carries an explicit `path` under that worktree; never omit `path` at the repo root.

Related: [[always-use-worktree]].
