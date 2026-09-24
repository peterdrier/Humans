---
name: The session subscribed to a PR never does the work
description: At ready-for-review, hand the PR to a fresh sonnet steward session (create_session) and stop; the steward only classifies wakes and dispatches one round worker per actionable event; it never builds, reads threads or logs, or fixes. See the `pd:steward` skill.
---

The session that built a PR does not tend it. At ready-for-review it spawns a steward
session with a brief of a few hundred tokens, unsubscribes, and stops. The steward only
reads notifications, classifies, and dispatches one `pd:orch-opus-medium` round worker per
actionable wake; the worker does the whole round and reports in a dozen lines.

**Why:** every PR wake re-reads the whole subscribed session before it can decide that
nothing happened. On peterdrier/Humans#1756 (2026-09-20) the builder carried 190k tokens
into 37 wakes of which 7 were actionable, and spent $57 on five review rounds, three
quarters of it re-reading its own history and deliberation; the fixes were made by
fresh-context workers anyway. Subagents cannot read the session's notification queue and
the session webhook rejects unsigned posts (both tested 2026-09-21), so event filtering
cannot be built repo-side; the cost has to come out of the wake itself.

**How to apply:**

- Hand-off, wake protocol, and the round worker's brief: the `pd:steward` skill (plugin
  `peterdrier/skills`); this repo's gate and triage rules: [`.claude/steward.md`](../../.claude/steward.md). `/pd:orch` does the hand-off as its last step.
- The steward never runs Bash, never fetches threads, diffs or logs, never edits, never
  replies in a thread, never schedules a check-in.
- A wake with nothing actionable ends the turn with no reply and no comment.
- `create_session` must pass `source_url: https://github.com/peterdrier/Humans` and
  `source_revision: <PR branch>`. A child session inherits the environment but **not** the
  parent's repositories: without a source it has no repo, `subscribe_pr_activity` fails with
  "this session has no repositories configured", and the PR goes untended (2026-09-23,
  peterdrier/Humans#1816). Have the steward report the subscribe result in its first reply and
  check it with `get_session` before ending the builder turn.
- Without `create_session` (local run), the builder stays subscribed and follows the same
  wake protocol itself.

**Related:** [`no-scheduled-pr-checkins`](no-scheduled-pr-checkins.md) ·
[`review-round-budget`](review-round-budget.md)
