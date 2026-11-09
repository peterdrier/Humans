# Maintenance dispatcher

The one scheduled task for Humans maintenance. It runs every 3 hours on a small model, reads state, starts at most a few jobs, and exits. It does no maintenance work itself beyond tagging Codex. Each job is a fire-only scheduled task that runs in its own session on a full model.

## Rules

- **Read, decide, exit.** No builds, no checkouts, no code reading.
- **GitHub is the memory.** "Already done" is decided from PRs, comments, and each job's last fire time, never from anything this run remembers. Two ticks in a row must make the same decision.
- **Fire, don't do.** A job starts with `fire_trigger` on the scheduled task named in the tables below, found by name in `list_triggers`. A job that's missing or disabled gets one line in the summary and is skipped.
- **Clean up after yourself.** End with at most 5 summary lines, then archive this run's own session (`get_session` with no id, then `archive_session`). Post nothing in the project unless something needs Peter.

Times are Europe/Madrid. A job "fired today" means its `last_run.fired_at` falls on today's Madrid date.

## 1. Tag Codex

Codex no longer reviews PRs by itself, so each PR gets one `@codex review` comment. For every open, non-draft PR on peterdrier/Humans not authored by Dependabot: if no comment contains `@codex review` and Codex has not reviewed it, comment `@codex review`. Never post it twice on one PR ([`pr-review-feedback-handling`](../../memory/process/pr-review-feedback-handling.md)).

## 2. Debt review

The Codex runner opens its `codex/daily-debt/<date>` PR only when its run has finished. Fire **Daily debt PR review** when all of these hold:

- an open `codex/daily-debt/*` PR exists;
- it has no comment starting `## Debt review` and no commit with a `Review-round:` trailer (a review already owns it);
- the job has not fired in the last 6 hours (a review may still be running).

## 3. Daily jobs

Fire each once per day, at the first tick at or after its time, if it has not fired today.

| Time | Task |
|------|------|
| 03:00 | Daily issue drain (peterdrier/Humans) |
| 07:00 | Daily triage (prod) |

## 4. Rotation

One maintenance PR per day at most. At the first tick at or after 07:00, look up today's entry. Fire it unless it has fired today, or a PR it opened earlier is still open (backpressure: that day is skipped, not queued).

| Day | Task | Its open PR is a branch starting |
|-----|------|----------------------------------|
| Sunday | Weekly dependency upgrades | `deps/weekly-` |
| Monday | Section doctor | `section-doctor/` |
| Tuesday | Section doctor | `section-doctor/` |
| Wednesday | Freshness sweep | `freshness-sweep/` |
| Thursday | Section doctor | `section-doctor/` |
| Friday | Section doctor | `section-doctor/` |
| Saturday | none | |

Monday also fires **Flaky test tracker** at the first tick at or after 16:00.

## Changing the schedule

Edit the tables in this doc. The scheduled tasks themselves hold only their own job's prompt and no schedule.
