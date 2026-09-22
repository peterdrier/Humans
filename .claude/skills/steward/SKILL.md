---
name: steward
description: How a PR opened from this repo is tended after ready-for-review. The session subscribed to the PR's events is a thin steward that classifies each wake and dispatches one round worker; it never does the work itself. Read before acting on any CI or review event on a PR you opened, and at hand-off time in /orch.
---

# Stewarding a PR in this repo

Everything here is repo convention. It cannot loosen any rule stated as a *never*, and it
cannot let you merge. Peter merges.

## Why the shape is what it is

A PR wake re-reads the whole subscribed session's context before it can decide whether
anything happened. Measured on peterdrier/Humans#1756 (2026-09-20): 37 wakes, 87 events,
7 wakes actionable; the orchestrator that built the PR carried 190k tokens into every one
of them and spent $57 on five review rounds, three quarters of it re-reading its own build
history and its own deliberation. The fixes were made by workers with fresh context; the
history bought nothing.

So three rules:

1. **The session that built the PR does not steward it.** At ready-for-review it hands the
   PR to a fresh session and stops.
2. **The steward session only classifies and dispatches.** Its context stays small, so a
   no-op wake costs cents. It never sees build output, threads, diffs or logs.
3. **One round worker does the whole round** and reports in a dozen lines.

## Hand-off (the builder's last act)

When the deliverable is pushed and the PR is ready for review:

1. Spawn the steward with `create_session` (Claude Code Remote tools):
   - `model`: `claude-sonnet-5`: classify-and-dispatch needs no judgement; the judgement
     lives in the round worker.
   - `title`: `steward: <owner>/<repo>#<N>` · `tags`: `["steward"]`.
   - `prompt`: the brief below, filled in. Nothing else: no history, no ledger.
2. Stay subscribed until the steward is. Wait for its first turn to finish (`get_session`
   on the new session) and confirm from its reply that `subscribe_pr_activity` succeeded.
   The brief overlap is deliberate: a duplicate wake costs one classification, while an
   event arriving in a gap where nobody is subscribed is lost for good — nothing polls.
3. Only then `unsubscribe_pr_activity` for the PR. From here the steward alone is
   subscribed. If the steward never confirms, do not unsubscribe: fall back below. A
   steward whose first subscribe failed and later succeeds on a retry owes the PR one
   catch-up check before waiting (wake protocol, step 0): events from the gap were
   never queued.
4. Report to the user in one line (PR URL, steward session id) and end.

Fallback, when `create_session` is not available (a local run without the remote tools):
stay subscribed and follow the wake protocol below yourself. Do not schedule check-ins
([`no-scheduled-pr-checkins`](../../../memory/process/no-scheduled-pr-checkins.md)).

Steward brief:

```
You are the steward for <owner>/<repo>#<N> (branch <branch>, base main).
Read .claude/skills/steward/SKILL.md now and follow its wake protocol; keep every
turn short. Deliverable, one paragraph: <what the PR does>.
Review rounds spent so far: 0 (verify against Review-round trailers via the worker).
First action: subscribe_pr_activity for this PR, then end the turn with one line
saying whether the subscription succeeded — the builder waits for that before
dropping its own. If it failed, retry when the tool becomes available; after a
retry succeeds, run the catch-up check (wake protocol, step 0) before waiting.
```

## The wake protocol (the steward's whole job)

On every wake:

0. **Catch-up, once, only after a late subscribe.** If `subscribe_pr_activity` did not
   succeed at spawn and a later retry did, anything posted in between was never queued.
   Right after that successful subscribe, make one read-only check of what is already on
   the PR — counts only, no bodies: unresolved review threads and review comments, failed
   checks on the current head, the head sha. Classify that like a drained queue (step 2)
   and dispatch per step 3; `Trigger: catch-up after late subscribe: <what the check
   found>`. A subscribe that succeeded on the first try skips this: nothing happened
   before it existed.
1. `ReadNotifications` until it reports 0 remaining. This is the one tool the steward
   must call itself: subagents cannot read the session's queue (tested 2026-09-21).
2. Classify without any other tool call. Actionable:
   - a review comment or review from anyone but this session's own account
   - a `check_run` or `check_suite` that failed on the PR's current head
   - a merge-conflict or base-branch-recovered notice
   - Peter, in a PR comment or here — classify which kind, they take different paths:
     a **question** (answer-only) or a **change request** (a worker that edits)
   Not actionable, end the turn with no reply and no comment: edits to bot status
   comments (Codex summary, preview deploy, surface report), echoes of the steward's or
   the worker's own replies, successful check suites, a Codex review wrapper with no
   comments, an event on a head the worker has already superseded.
3. Actionable: dispatch **one** worker per the brief in
   [`round-worker.md`](round-worker.md), `orch-opus-medium`, and end the turn. A review
   finding, CI failure, merge conflict or change Peter asked for gets the round worker; a
   **question** from Peter gets that brief marked `Answer only: no triage, no edit, no
   commit` — it answers in a reply and touches nothing. Never two workers on one PR at
   once, and one wake often drains several actionable events while only one of them can be
   dispatched. Carry **every** actionable event you do not dispatch — the rest of this
   wake's drain as much as anything arriving while a worker runs — in your reply as a
   pending trigger, and work that list down one worker at a time. The queue is drained once
   and nothing polls, so an unrecorded event is a lost finding.
4. On the worker's report: keep one line of state in your reply (head sha, rounds spent,
   open items, pending triggers). If it carries an `ANSWER (relay verbatim):` block — a
   question Peter asked here rather than in a PR comment, so the worker had no thread to
   reply in — put that block in your reply as-is, above the state line: nothing else
   carries the answer back to him. If the report says the ceiling is reached, post its
   ceiling comment on the PR, `unsubscribe_pr_activity`, and stop. If it is
   `STATUS: blocked`, post its `OPEN` items on the PR as a comment addressed to Peter and
   stay subscribed — the builder is gone and nothing polls, so an unposted decision never
   reaches him and his reply is what wakes you. Otherwise, if any pending
   trigger is still uncovered by the report, dispatch one fresh worker for the oldest of
   them now (it recounts rounds itself, so the ceiling still holds), carry the remainder
   forward in your reply, and end the turn; with none, just end the turn.

The steward **never**: runs Bash; reads threads, diffs, logs or files; edits code; drafts a
fix; replies in a thread (the worker does); schedules a check-in; raises the ceiling;
merges.

## Rounds and the ceiling

A **round** is a commit pushed in response to an automated review finding or a CI
failure, from the point the PR's own work is functionally complete, carrying a
`Review-round: <n>` trailer. Finishing the deliverable, doing what Peter asked, and
mechanical commits (base merges, conflict resolution) are not rounds and carry no trailer.
Nor do they reset or raise the ceiling. The trailer is the memory, not the definition:
where trailers and the PR's review history disagree, the history wins
([`review-round-budget`](../../../memory/process/review-round-budget.md)).

The worker counts spent rounds from the PR, never from memory:

    gh pr view <N> --repo <owner>/<repo> --json commits \
      --jq '[.commits[] | select((.messageBody // "") | test("(?m)^Review-round: [0-9]+"))] | length'

(or `pull_request_read get_commits` where `gh` is absent). Always pass the owner: fork and
upstream reuse PR numbers. The number is evidence, not the answer: check it against the
PR's review history — the bot reviews and red checks that came before it — on every count,
not only when it reads zero. A rebase or a squash drops some trailers and keeps others, so
a plausible nonzero count can still be short, and a zero on a PR with bot reviews or an
earlier red check is a missing record, not a fresh budget. Where the two disagree, count
the round commits by hand and use that.

The unattended ceiling is **five review-round commits per PR**:

| Spent | What the round worker does |
|---|---|
| **0–3** | Normal round. Verify, judge, fix in scope, one commit. |
| **4** | Last commit. Read every open finding first, spend it on the most serious one. Say in the commit message that the budget is spent. |
| **5+** | No triage, no patch. Draft the ceiling comment (open items, what you'd do about each, what needs deciding) and return it; the steward posts it, unsubscribes and stops. |

Judgment still applies inside the ceiling: stop the moment re-reviews stop surfacing real
new problems. Five is a ceiling, not a target, and only Peter raises it, in his own words.
Waiting on Peter is the finish, not a failure to finish.

## The round itself

Findings are hypotheses, not a work list: [`/fix`](../fix/SKILL.md) runs the gates,
[`review-finding-triage`](../../../memory/process/review-finding-triage.md) and
[`pr-review-feedback-handling`](../../../memory/process/pr-review-feedback-handling.md)
carry the posture and the mechanics (every finding ends with a disposition reply in its
own thread, then resolve). Three things the worker holds to:

- **Count the bug class, not the finding.** The third fix of one class on one PR is a
  design error: change the shape so the class cannot recur, don't add a third guard.
- **A fix that opens the next hole, twice in a row, means stop.** The invariant is
  underspecified; escalate the rule, not the line.
- **One round is one commit**, with its trailer. Splitting a round across three commits
  spends three.

The reviewers are bots with no memory between rounds. A bot is a generator, not a reviewer
working toward done; deciding the review is over is the worker's job, within the table
above.

## Never

- Merge. Peter merges.
- Raise the five-round ceiling, or push past it, on your own judgement.
- Put a `Review-round:` trailer on a commit that is not a round, or leave it off one that is.
- Skip, disable or quarantine a test to get CI green.
- Push without `dotnet build Humans.slnx -v quiet` clean and `dotnet test Humans.slnx -v quiet` green.
- Steward a PR from the session that built it when `create_session` is available.
