# Round worker brief

The steward dispatches one of these per actionable wake, as `orch-opus-medium`. Fill the
angle brackets; paste nothing else. The worker starts with zero context and reads what it
needs from the repo and the PR.

```
Review round on <owner>/<repo>#<N>, branch <branch>. Rounds spent before this one: <k>.
Trigger: <one line: "Codex review on <sha>, comment ids …" | "CI check <name> failed on <sha>" | "merge conflict with main" | "Peter: <his words>">.

Read .claude/skills/steward/SKILL.md (sections "Rounds and the ceiling" and "The round
itself") and .claude/skills/steward/round-worker.md, then do the whole round yourself.
Report file: <scratchpad>/steward/<N>-round-<k+1>.md.
```

## What the worker does

1. **Workspace.** In a cloud container the repo root is fine. Locally, `git worktree list`
   first: the branch is usually already checked out in the builder's worktree, and
   `git worktree add` on a branch checked out elsewhere fails outright — reuse that
   worktree, and only add `.claude/worktrees/steward-<N>` when no worktree holds the
   branch. Fetch fresh, work only there
   ([`always-use-worktree`](../../../memory/process/always-use-worktree.md)).
2. **Count.** Recount spent rounds from the PR (command in SKILL.md). Where the count and
   the brief disagree, the PR wins; say so in the report. At 5 or more: skip to step 7.
3. **Merge conflict first.** Merge main into the branch with a merge commit, resolve,
   regenerate generated files with the repo's tooling, never rewrite history. Not a round.
4. **CI failure.** Read the failed job's log tail once. Rule out a failure that isn't this
   PR's: a test the diff doesn't touch that hit a timeout under runner load, or a check red
   on main too. That gets one re-run (or the next push serves as one) and no commit. A
   real failure in code the PR touches is a round: fix it.
5. **Findings.** Run the [`/fix`](../fix/SKILL.md) gates on every unresolved thread, both
   repos, and print its triage table into the report file. Fix only what survives and sits
   inside the ceiling table's bar for this round. `gh` where present; otherwise the github
   MCP tools (`pull_request_read get_review_comments`, `add_reply_to_pull_request_comment`,
   `resolve_review_thread`).
6. **One commit, then the gate.** All the round's fixes in one commit whose message ends
   with `Review-round: <k+1>` and the session's attribution trailers. Then
   `dotnet build Humans.slnx -v quiet` and grep `Error(s)` is 0, then
   `dotnet test Humans.slnx -v quiet --no-build` with no `Failed!`; never trust a test run
   against a stale build. Push by URL ([`push-by-url-in-cloud`](../../../memory/process/push-by-url-in-cloud.md)).
   Then reply in every thread with its disposition and the sha, react, resolve; leave open
   only a thread genuinely waiting on Peter.
7. **Ceiling.** At 5 spent, or when the table says stop: write the ceiling comment into
   the report file (what is open, what you'd do about each, what needs deciding, ending
   with the Claude Code footer) and return `CEILING`.

## What the worker never does

- Steward: it does not subscribe, read notifications, or wait for anything.
- Push more than one round commit, or a commit without its trailer.
- Skip, disable or quarantine a test; use `--no-verify`; hand-edit state.
- Merge, or ask Peter whether to fix a finding: the gates decide.
- Put logs, diffs or file contents in the reply.

## Return contract

Reply in at most 12 lines:

```
STATUS: done | ceiling | blocked
HEAD: <sha pushed, or "no push">
ROUNDS SPENT: <n after this round>
TRIGGER HANDLED: <one line>
FINDINGS: fixed <a> · declined <b> · deferred/filed <c> · left open for Peter <d>
CI: <green on <sha> | red: <check>, <why it is not this PR's> | not yet run>
OPEN: <anything the steward must relay to Peter, one line each, or "none">
REPORT: <path>
```
