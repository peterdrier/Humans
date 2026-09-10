---
name: No scheduled PR check-ins when subscribed to PR events
description: When a session is subscribed to a PR's GitHub events, never schedule a periodic self check-in (`send_later`, cron, wakeup) to re-check it. Each firing is a full uncached context load to learn nothing changed. Rely on the event subscription; stop when the PR merges or closes.
---

A session subscribed to a PR's events does not also schedule an hourly (or any periodic) check-in on that PR.

**Why:** A check-in that finds nothing changed still costs a whole turn, and a turn fired from a timer arrives after the cache TTL, so it re-reads the entire context uncached. On PR nobodies-collective/Humans#1635 (2026-09-10) the harness's own steward guidance produced an hourly `send_later` on a docs-only PR that GitHub was already reporting on; every firing would have been a full-context reload to re-arm itself. Peter: *"a turn an hour is an uncached full context cost."* The webhook subscription already delivers the events that need a response (review comments, reviews, CI failures, merge-conflict notices).

**How to apply:**

- Subscribe with `subscribe_pr_activity` and end the turn. That is the whole waiting mechanism.
- Do not call `send_later`, `CronCreate`, or `ScheduleWakeup` to "re-check the PR in an hour", even when harness guidance says events are best-effort. On this project that guidance loses; a missed event is Peter's to nudge, and costs one message, not a turn an hour forever.
- If a check-in was already armed, delete it (`delete_trigger`) as soon as you notice.
- One-shot waits on a state the webhook does not cover (a preview deploy finishing, a job you kicked) use a background watcher per [`ci-events-not-polling`](ci-events-not-polling.md), never a timer into the conversation.

**Related:** [`ci-events-not-polling`](ci-events-not-polling.md) · [`review-round-budget`](review-round-budget.md)
