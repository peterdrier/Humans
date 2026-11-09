# Maintenance dispatcher

The one scheduled task for Humans maintenance. It runs in the Humans Development project every 3 hours on a small model. It reads state, asks for a project thread per job that is due, and exits. Apart from tagging Codex, it does no work itself.

## Rules

- **Read, decide, exit.** No builds, no checkouts, no code reading.
- **Start a job** by asking the coordinator (`message_coordinator`) for a thread with exactly this text: `Start a thread titled "<Job> YYYY-MM-DD" with this instruction: follow docs/maintenance/<doc> as an unattended run.` The thread does the work on the project's default model.
- **Started today** means `list_threads` (since 00:00 Madrid today) shows a thread titled `<Job> YYYY-MM-DD` for today's date. Never start a job twice in one day; two ticks in a row must make the same decision.
- **Clean up after yourself.** Post nothing in the project unless something needs Peter. End with at most 5 summary lines, then archive this run's own session (`get_session` with no id, then `archive_session`).

All times are Europe/Madrid.

## 1. Tag Codex

Codex no longer reviews PRs on its own, so each PR gets one `@codex review` comment. For every open, non-draft PR on peterdrier/Humans not authored by Dependabot: if no comment contains `@codex review` and Codex has not reviewed it, comment `@codex review`. Never post it twice on one PR ([`pr-review-feedback-handling`](../../memory/process/pr-review-feedback-handling.md)).

## 2. Debt review

The Codex runner opens its `codex/daily-debt/<date>` PR only when its run has finished. Start **Debt review** (`debt-review/README.md`) when all of these hold:

- an open `codex/daily-debt/*` PR exists;
- it has no comment starting `## Debt review` and no commit with a `Review-round:` trailer (a review already owns it);
- no Debt review thread was started today.

## 3. Daily

At the first tick at or after 03:00, start **Issue drain** (`issue-drain.md`) unless it was started today.

## 4. Rotation

At most one maintenance PR a day. At the first tick at or after 07:00, look up today's entry and start it, unless it was started today or a PR it opened earlier is still open. With an open PR, skip the day (backpressure); nothing is queued.

| Day | Job | Doc | Its open PR's branch starts |
|-----|-----|-----|-----------------------------|
| Sunday | Dependency upgrades | `dependency-upgrades.md` | `deps/weekly-` |
| Monday | Section doctor | `section-doctor/README.md` | `section-doctor/` |
| Tuesday | Section doctor | `section-doctor/README.md` | `section-doctor/` |
| Wednesday | Freshness sweep | `freshness-sweep/README.md` | `freshness-sweep/` |
| Thursday | Section doctor | `section-doctor/README.md` | `section-doctor/` |
| Friday | Section doctor | `section-doctor/README.md` | `section-doctor/` |
| Saturday | none | | |

## Not dispatched

Prod triage keeps its own scheduled task, because it needs the production API key in its own environment.

## Changing the schedule

Edit this doc. The dispatcher's scheduled task only says to follow it.
