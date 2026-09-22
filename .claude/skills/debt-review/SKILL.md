---
name: debt-review
description: "Review, repair and steward the nightly Codex debt-sweep PR (branch codex/daily-debt/<date>). Judges every commit GOOD / REPAIR / REVERT, triages the bot review findings through /fix, lands one round-1 commit, files follow-up issues, then stewards the PR itself with a 3-round ceiling. Run by the 08:00 UTC cloud routine; also '/debt-review', '/debt-review 1789'."
argument-hint: "(none — today's codex/daily-debt PR) | 1789"
---

# Debt review — validate the nightly Codex PR

The Codex runner (`.codex/cron/run-daily-debt.sh`) opens one PR a night on
`codex/daily-debt/<YYYY-MM-DD>`, ~25 commits, built and tested but never reviewed.
Green is not good: this skill decides which commits are worth keeping, repairs or
reverts the rest, deals with the bot findings, and gets the PR mergeable for Peter.
Peter merges. Never merge, never force-push, never rewrite the branch's history.

Budget: **3 review rounds total** for this PR (Peter, 2026-09-22), and this run's commit
is round 1.

## 1. Find the PR — or stop

`$ARGUMENTS` names a PR number; otherwise take the newest open `codex/daily-debt/*` PR
(not just today's: a late runner can open it after 08:00, and it skips nights while one is
open):

```bash
gh pr list --repo peterdrier/Humans --state open --limit 200 --json number,headRefName,createdAt   --jq '[.[] | select(.headRefName | startswith("codex/daily-debt/"))] | sort_by(.createdAt) | last'
```

Stop with one line, touching nothing, when: no PR; it is not open; or it already has a
commit carrying a `Review-round:` trailer or a comment starting `## Debt review` (a
previous run owns it). Otherwise `subscribe_pr_activity` for it now, before any review
work, so no bot event between here and §6 is lost. In a cloud run (`CLAUDE_CODE_REMOTE=true`) check out the head
branch in the repo root; locally use a worktree under `.claude/worktrees/`. Push by URL
([`push-by-url-in-cloud`](../../../memory/process/push-by-url-in-cloud.md)).

## 2. Judge every commit

List the PR's non-merge commits and pair each with its line in the PR body (the claimed
intent). Fan out: one `pd:orch-opus-medium` worker per ~6 commits, read-only, each given the
commit shas, their PR-body lines, and this brief:

> For each commit: read `git show <sha>` and the code around it at the PR head (not just
> the hunk). Verdict **GOOD**, **REPAIR** (right idea, specific defect, fix is small and
> certain — say exactly what) or **REVERT** (wrong, unsafe, or net-negative). Judge
> against `AGENTS.md`, `docs/architecture/peters-hard-rules.md`,
> `docs/architecture/code-review-rules.md`, and every `memory/INDEX.md` atom whose trigger
> matches the files (read the atom). Specifically: behaviour actually preserved or
> actually fixed; no other section's tables; no new public surface without a defensible
> reason; user-facing strings in all six cultures with section-prefixed keys; invariant
> doc updated for behaviour changes; tests not weakened; no dead code left behind
> (orphaned constants, unused overloads); not bookkeeping dressed as a fix. Default to
> GOOD for a plain, correct change — style is not a defect. Return a table
> `sha | claim | verdict | reason (≤20 words) | repair (if REPAIR)` and nothing else.

Spot-check any REVERT or REPAIR verdict yourself against the code before accepting it.
A REPAIR you are not certain of is a REVERT. A reverted commit's ledger/doc edits go with
it — `git revert` handles that.

## 3. Triage the bot findings

Run the [`/fix`](../fix/SKILL.md) gates on every unresolved thread (Codex, Claude bot,
Gemini; both repos) at round 1. A finding on a commit you are reverting is resolved as
`REVERTED — <sha> reverted in <new sha>`, no other triage. A finding that points at the
same defect as a REPAIR verdict is fixed by that repair. Print the /fix triage table.

Follow-up issues (the /fix §5 criteria: real, P2+, not already tracked, out of this
PR's scope) — plus any debt a REVERT verdict leaves unfixed that is worth doing properly —
go on `peterdrier/Humans` ([`issue-home-routing`](../../../memory/process/issue-home-routing.md);
a cloud run cannot write upstream), each with a `**Section:**` line and a link to the
thread or commit.

## 4. One commit, then the gate

`git revert --no-commit` each REVERT (newest first), apply every REPAIR and every /fix
`FIX`, then one commit:

```
Debt review: revert <n>, repair <m>, fix <k> findings

Reverted: <sha> <claim> — <reason>
Repaired: <sha> <claim> — <what>
Fixed: <file:line> <finding>

Review-round: 1
```

Gate: `dotnet build Humans.slnx -v quiet` with 0 errors, then
`dotnet test Humans.slnx -v quiet --no-build` with no `Failed!`
(`Humans.Integration.Tests` self-skip). Red → fix it inside this same commit; if a repair
can't go green, turn it into a revert. Never push red. Nothing to change → no commit.

Push, then reply in every thread with its verdict and the sha, react, resolve (mechanics:
[`pr-review-feedback-handling`](../../../memory/process/pr-review-feedback-handling.md)).

## 5. Report on the PR

One PR comment, starting `## Debt review`:

- the commit verdict table (all commits, one row each)
- the /fix triage table
- issues filed, owner-qualified
- `Rounds spent: 1 of 3` and the pushed sha
- a one-line recommendation: **merge**, **merge after steward rounds**, or **close**
  (more than half the substantive commits reverted)

## 6. Steward it yourself

No hand-off: this session is the steward (subscribed since §1). End the
turn with the summary below, and on every wake follow the `pd:steward` skill's
wake protocol, with the [`.claude/steward.md`](../../steward.md) overlay, with `Ceiling: 3` (rounds spent: 1, or 0 if §4 pushed nothing). Round
workers are `pd:orch-opus-medium` via Task, per the round-worker brief with `Ceiling: 3`. At the ceiling, or
when the PR is merged or closed, `unsubscribe_pr_activity` and stop.

Summary: PR URL, verdict counts (good / repaired / reverted), findings
fixed / declined / filed, issues filed, pushed sha.
