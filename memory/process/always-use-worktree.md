---
name: always-use-worktree
description: HARD RULE on a local machine (many agents share one clone): all branch work happens in a `.worktrees/<name>` worktree, the main checkout is read-only, and every Glob/Grep carries an explicit path. In a Claude Code cloud run (`CLAUDE_CODE_REMOTE=true`) it does not apply — the container is single-session and ephemeral, so work in the repo root on the designated branch
---

Which half of this rule applies is decided by the environment, once, before any exploration:

```bash
[ "$CLAUDE_CODE_REMOTE" = "true" ] && echo cloud || echo local
```

A cloud run also sets `CLAUDE_CODE_REMOTE_ENVIRONMENT_TYPE=cloud_default`; `CLAUDE_CODE_REMOTE` alone is the check.

## Local machine — HARD RULE, worktree

ALL branch work — including work on `main` itself — happens in a git worktree under `.worktrees/<name>`. The main checkout is read-only: never edit files, stage, commit, reset, or checkout there.

**Why:** Peter's machines run 5–10 agent sessions against one clone. At any moment another process may have uncommitted changes on `main` there — resetting or switching branches destroys that work, and an uncommitted edit can be wiped if something else switches branches mid-edit. Peter: "you ARE NEVER ALLOWED TO WORK IN MAIN" — applies to the main checkout for any branch, including main.

**How to apply:**
- New work: `git worktree add -b <slug> .worktrees/<slug> origin/main`, commit on that branch, push it, open a PR ([[no-direct-to-main]] — the standalone `memory/**` direct-main case is defined there, and even that is pushed from the worktree, never from the main checkout).
- Existing branch: `git worktree add .worktrees/<slug> <branch>`.
- Enter it with the `EnterWorktree` tool — it moves the session's working directory into the worktree, so Bash calls stop needing their own `cd` and can't silently revert to the root between calls. `ExitWorktree` returns. Fall back to `cd` once and staying there only where that tool isn't available.
- Never `git checkout`, `git reset`, `git stash`, or delete/move tracked files in the main checkout.
- If the main checkout has uncommitted changes on arrival, assume they belong to another agent/process and leave them alone.
- When a skill finds the current branch differs from a target PR's head branch, the answer is always "use a worktree" — check `git worktree list` for an existing one first, or create one. Don't present it as a multi-option question.
- Sole exception: the post-production-merge fork reset in [[after-prod-merge-reset]], run exactly as that atom says and nothing more.

### Scope every search to the worktree (local only)

Create the feature worktree **first**, before any code exploration, then do all reading/searching/editing inside it. **Never run `Glob`/`Grep` against the repo root.** Full checkouts live under it in multiple places — `.worktrees/` (feature worktrees) and any nested agent worktree directories — and the count moves constantly as parallel sessions create and clean them. Each is a complete copy of the codebase at a different commit, so an unscoped search returns duplicate hits per file from mutually inconsistent revisions — that's what poisons the search, at any count.

`Glob`/`Grep` ignore the Bash `cd` — they default to the repo root — so you MUST pass an explicit `path` every time, scoped to `<worktree>/src` and `<worktree>/tests` (subtrees that hold no nested worktrees). `EnterWorktree` does not change this; only a search rooted inside the worktree is safe.

**Why:** on one issue, exploring the main checkout for many turns before making the worktree, then globbing the polluted root repeatedly, burned tens of thousands of tokens on worktree-noise alone.

**How to apply:** step 1 of any feature task — after the issue-authorization preflight in [[issue-fetch-protocol]] for issue-driven work — is `git worktree add -b <name> .worktrees/<name> origin/main`. From then on, every Glob/Grep carries an explicit `path` under that worktree; never omit `path` at the repo root.

## Cloud run — repo root, no worktree

Work directly in the repo root, on the branch the session was designated. Do not create a worktree.

**Why:** the premise the local rule rests on is false here. The container is ephemeral, the repository is cloned fresh at session start, exactly one session owns it, and the container is reclaimed afterwards — there is no other agent to trample and no pre-existing uncommitted work to destroy. Applying the rule anyway costs real turns and buys no isolation: on `peterdrier/Humans#1525` every Bash call needed its own `cd` (the shell's cwd reverts to the session root between calls), one `git commit` landed after a silent revert and cost a turn to trace, and the mandatory `path=` on every search existed only to work around pollution the worktree itself created. Peter: "that rule is specific to running on any of my machines… in the case of these cloud runs though, that doesn't apply, and the rule is fundamentally invalid."

**How to apply:**
- `git checkout -b <designated-branch>` (or check out the existing one) in the repo root and work there. Nothing needs a `cd`.
- `Glob`/`Grep` need no explicit `path` — there are no nested checkouts to pollute the root. Pass one when you want to narrow a search, not as ritual.
- Branch and PR discipline is unchanged: still a feature branch, still a PR ([[no-direct-to-main]]). Working in the repo root is not permission to commit to `main`.
- The one case that still wants worktrees in the cloud: a skill running genuinely parallel lanes that must not share a checkout (`/refactor-swarm`, [[lanes-branch-off-main]]). Isolation there is between this session's own lanes, not against other sessions, so the reason survives.

**Reading the rest of the rules.** Atoms, skills and agent prompts were written local-first, so "the worktree" is the ordinary word for the workspace throughout them. In a cloud run read every such mention as **the repo root**, and every `git worktree add` in a procedure as the `git checkout -b` that reaches the same branch — this atom is the authority, not the local phrasing downstream of it. Only two things are then genuinely different, and both are no-ops rather than substitutions: there is no second checkout to keep clean or to diff against, and there is nothing to tear down — never run `git worktree remove`, which in a cloud run would be aimed at the repo root itself. A collision that a local run would resolve against `git worktree list` is a plain branch collision here (`git branch --list`).

Related: [[worktrees-off-origin-main]], [[pr-fix-switches-to-pr-branch]], [[agent-isolation-worktree-base]].
