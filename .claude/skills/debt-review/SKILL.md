---
name: debt-review
description: "Review, repair and steward the nightly Codex debt-sweep PR (branch codex/daily-debt/<date>). Judges every commit GOOD / REPAIR / REVERT, triages the bot review findings through /fix, lands one round-1 commit, files follow-up issues, then stewards the PR itself with a 3-round ceiling. Also opens at most one flagged ⚠️ DESTRUCTIVE drop PR a night for a retirement already on production; Peter merges it. Run by the 08:00 UTC cloud routine; also '/debt-review', '/debt-review 1789'."
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
work, so no bot event between here and §7 is lost. In a cloud run (`CLAUDE_CODE_REMOTE=true`) check out the head
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

**Production readings — verify, never trust.** For every commit that retires a data
migration (deletes a registration, its legacy fallback or its obsolete fields), fetch
`https://humans.nobodies.team/api/data-migrations` yourself and quote the live reading in the
§5 report. A key that is missing or reads anything but `"pending":0,"done":true` makes that
commit a REVERT. Codex's quoted reading is a claim, not evidence.

Every retirement you keep leaves storage to drop. Its objects go in an open
`db:Destructive` issue on `peterdrier/Humans`: add them to one that is already open, or file
one naming each object, its owner context, the migration key, the reading you quoted and the
retiring commit's sha. These issues are §6's queue and don't count toward the three-issue cap.

Spot-check any REVERT or REPAIR verdict yourself against the code before accepting it.
A REPAIR you are not certain of is a REVERT. A reverted commit's ledger/doc edits go with
it — `git revert` handles that.

## 3. Triage the bot findings

Run the [`/fix`](../fix/SKILL.md) gates on every unresolved thread (Codex, Claude bot,
Gemini; both repos) at round 1. A finding on a commit you are reverting is resolved as
`REVERTED — <sha> reverted in <new sha>`, no other triage. A finding that points at the
same defect as a REPAIR verdict is fixed by that repair. Print the /fix triage table.

**Ledger first.** The runner prints `Open rows: <before> → <after>` under `## Ledger`.
Check that the run worked already-found rows before new finds, and that each row it added
says why it could not be fixed tonight — if you can fix one here, do. A row that was only
reworded or moved is not a close.

**Re-check Needs-Peter rows, then elevate.** Codex's **Needs Peter** list is a claim, not
a verdict — something Codex couldn't fix may be fixable here. Take the list in order and
verify each row against the code. Apply [`debt-sweep-standing-policy`](../../../memory/process/debt-sweep-standing-policy.md)
(D1–D13) first: a rule-prescribed contract change is pre-approved (D1), `internal` types are
never public surface (D2), and D5–D9/D13 rows close outright. Elevate a row only when it truly
needs Peter's decision (a NEW interface, service or endpoint, or a policy no rule answers),
never because the fix looked hard:

- **Fixable without a decision** (including D1 shapes) → fix it in the round-1 commit and
  delete the row; list D1 changes under **Pre-approved contract changes** in the PR body.
- **Stale** (already fixed) or **D5 close / D6–D9 not debt** → delete the row.
- **Truly needs Peter** → write it in D3 format, answerable cold (what the code does today,
  what would change, why it matters, a yes/no proposal with a concrete signature — never the
  row id alone); search for an existing issue first (cite it instead of filing),
  file the rest, and delete each elevated row in the round-1 commit with the issue in the
  commit message. **At most three new issues per review, follow-ups below included** —
everything past the cap stays in the ledger for a later night. Never bulk-file.

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
- the live production reading for each retired data migration, fetched this run
- the /fix triage table
- issues filed, owner-qualified, and the rows they replaced; the `db:Destructive` issue each kept retirement went to
- the ledger line: open rows before → after this review
- `Rounds spent: 1 of 3` and the pushed sha
- a one-line recommendation: **merge**, **merge after steward rounds**, or **close**
  (more than half the substantive commits reverted)

## 6. Open the drop PRs that are due

The queue is the open `db:Destructive` issues on `peterdrier/Humans` that came from §2, from
any night. An issue is **due** when its retiring commit is running on production: read
`commit` from `https://humans.nobodies.team/api/version`, fetch
`https://github.com/nobodies-collective/Humans.git main`, and check
`git merge-base --is-ancestor <retiring sha> <prod commit>`. Skip an issue that isn't due, or
that already has an open PR, and list it in the summary. Open nothing while
[`event-deploy-freeze`](../../../memory/process/event-deploy-freeze.md) applies.

Open **at most one drop PR a night**, for the oldest due issue, following the standing-approval
path in [`no-drops-until-prod-verified`](../../../memory/architecture/no-drops-until-prod-verified.md):

- Branch `db-drop/<issue>` off `origin/main`, separate from the debt PR.
- Delete what the issue lists: the frozen properties and mappings, the `Legacy*` enums, the
  erasure lines, and the doc rows.
- Run a full build, then `dotnet ef migrations add` in the owner context. Read the `Down()`.
- Add the baseline entries. `Approval:` cites the production reading in the issue and the atom.
  `Evidence:` cites the retiring PR and the production commit that contains it.
- Gate on the §4 build and tests, then run the
  [`ef-migration-reviewer`](../../agents/ef-migration-reviewer.md) agent. If either fails, don't
  open the PR: comment on the issue saying why.

**The PR has to say it is dangerous**, in the title, the label and the body's first lines:

- Title: `⚠️ DESTRUCTIVE: drop <objects> (<context>)`. Label: `db:Destructive`. Open it ready
  for review, with `Closes #<issue>` in the body.
- The body opens with this block, ahead of everything else:

  > [!CAUTION]
  > **This PR permanently deletes production data.** Deploying it runs `DROP` against
  > production. Reverting the code doesn't bring the data back; the only way back is
  > [`docs/database-restore-runbook.md`](../../../docs/database-restore-runbook.md).
  > Opened automatically by debt-review. Only Peter merges it.

- Then one row per dropped object: table and column, what it held, and what replaced it.
  After that, the production reading and its date, the deployed-commit check, and any
  caveat about `Down()` not running.
- Never merge it, never enable auto-merge, and never add `ready-to-merge`. The standing
  approval covers opening this PR, not merging it.

Subscribe to the drop PR and steward it under §7 with its own `Ceiling: 3`.

## 7. Steward it yourself

No hand-off: this session is the steward (subscribed since §1). End the
turn with the summary below, and on every wake follow the `pd:steward` skill's
wake protocol, with the [`.claude/steward.md`](../../steward.md) overlay, with `Ceiling: 3` (rounds spent: 1, or 0 if §4 pushed nothing). Round
workers are `pd:orch-opus-medium` via Task, per the round-worker brief with `Ceiling: 3`. At the ceiling, or
when the PR is merged or closed, `unsubscribe_pr_activity` and stop.

Summary: PR URL, verdict counts (good / repaired / reverted), findings
fixed / declined / filed, issues filed, pushed sha, and the drop PR opened (⚠️, with its URL)
or why none was due.
