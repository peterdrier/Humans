<!-- freshness:triggers
  src/Sections/Humans.Email/**
  src/Sections/Humans.Email.Contracts/**
  src/Sections/Humans.Email/Jobs/ProcessEmailOutboxJob.cs
  src/Sections/Humans.Email/Jobs/CleanupEmailOutboxJob.cs
  src/Sections/Humans.Settings/Domain/Setting.cs
  src/Sections/Humans.Settings.Contracts/SettingKeys.cs
-->
<!-- freshness:flag-on-change
  Outbox queue/process/cleanup behavior, retry/backoff config keys, global pause toggle, and admin dashboard route — review when OutboxEmailService, outbox jobs, or EmailController change.
-->

# Feature 21: Email Outbox

## Business Context

Transactional emails (onboarding, campaign codes, notifications) must be delivered reliably. Sending inline during a request risks data loss if the mail server is temporarily unavailable. The outbox pattern decouples email creation from delivery: all emails are first persisted to a database table, then a background job processes and delivers them. This guarantees that even if the SMTP server is down, emails are retried until delivered (or exhausted).

## How It Works

1. The sending section builds a rendered `EmailMessage` in its own `<Section>Emails` builder and calls `IEmailService.SendAsync(message)` instead of sending directly.
2. The service writes an `EmailOutboxMessage` row with `Status = Queued`.
3. `ProcessEmailOutboxJob` (Hangfire, runs every minute) picks up batches of queued messages and delivers them via `IEmailTransport`.
4. On success: `Status = Sent`, `SentAt` stamped.
5. On failure: `RetryCount++`, `LastError` set, `NextRetryAt` computed with exponential backoff. After `OutboxMaxRetries` attempts, message stays `Failed` and is not retried.
6. `CleanupEmailOutboxJob` (Hangfire, weekly — Sunday 03:00 UTC) deletes **sent** messages older than `OutboxRetentionDays`. `Failed` messages are retained (`DeleteSentOlderThanAsync` filters on `Status == Sent`).

## Configuration

All settings live under the `Email` section in `appsettings.json`:

| Setting | Default | Purpose |
|---------|---------|---------|
| `OutboxBatchSize` | 10 | Max messages processed per job run |
| `OutboxMaxRetries` | 10 | Max delivery attempts before marking Failed |
| `OutboxRetentionDays` | 150 | Days to keep sent/failed messages before cleanup |

## Admin Dashboard

Route: `/Email/EmailOutbox` — requires Admin role.

Features:
- Stats: queued count, sent in last 24h, failed count
- Link to the pause/resume toggle, which lives on `/Settings#email`
- Message table: recent messages with status, recipient, subject, retry count, last error
- Per-message retry button (resets a Failed message back to Queued)
- Per-message discard button (deletes the row)

When paused, `ProcessEmailOutboxJob` skips processing without dequeuing messages.

## Global Pause

The `IsEmailSendingPaused` key in `system_settings` controls whether the outbox processor runs. The Pause/Resume actions on the `/Settings#email` tab update this setting (peterdrier/Humans#1634); the dashboard only links to them. Useful during maintenance windows or when diagnosing delivery issues.

## Daily Send Counts (nobodies-collective/Humans#1195)

`email_daily_send_counts` (composite PK `(Date, TemplateName)`, `Date` UTC calendar day) tracks `SentCount`/`FailedCount` per template per day — a durable denominator that survives outbox retention pruning. `EmailOutboxProcessor` increments it right after each `MarkSentAsync`/`MarkFailedAsync`, sharing the same test-address exclusion; the increment is its own try/catch, swallowed on failure so a tally write never flips a delivered message back to `Failed`. Never purged — `CleanupEmailOutboxJob` only deletes from `email_outbox_messages`.

The dashboard (`/Email/EmailOutbox`) shows the last 90 days in a Date/Sent/Failed table.

**Backfill:** `GET /Email/EmailOutbox/BackfillDailyCounts` previews rows to add (count, date range, first 50 rows); `POST` confirms. Aggregates retained `Sent` outbox rows by `SentAt`'s UTC date; `FailedCount` stays `0` (failure day isn't reconstructable from the outbox). Excludes today's UTC date entirely. Only inserts (Date, TemplateName) combinations with no existing row — idempotent, re-running is a no-op past the first pass. Audited as `AuditAction.EmailDailySendCountsBackfilled`.

## Metrics (OpenTelemetry)

| Metric | Type | Description |
|--------|------|-------------|
| `humans.email_outbox_pending` | Gauge | Current queued message count |
