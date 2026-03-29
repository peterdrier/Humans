# Feature 21: Email Outbox

## Business Context

Transactional emails (onboarding, campaign codes, notifications) must be delivered reliably. Sending inline during a request risks data loss if the mail server is temporarily unavailable. The outbox pattern decouples email creation from delivery: all emails are first persisted to a database table, then a background job processes and delivers them. This guarantees that even if the SMTP server is down, emails are retried until delivered (or exhausted).

## How It Works

1. Application code calls `IOutboxEmailService.QueueAsync(...)` instead of sending directly.
2. The service writes an `EmailOutboxMessage` row with `Status = Queued`.
3. `ProcessEmailOutboxJob` (Hangfire, runs every minute) picks up batches of queued messages and delivers them via `IEmailTransport`.
4. On success: `Status = Sent`, `SentAt` stamped.
5. On failure: `RetryCount++`, `LastError` set, `NextRetryAt` computed with exponential backoff. After `OutboxMaxRetries` attempts, message stays `Failed` and is not retried.
6. `CleanupEmailOutboxJob` (Hangfire, runs daily) deletes sent/failed messages older than `OutboxRetentionDays`.

## Configuration

All settings live under the `Email` section in `appsettings.json`:

| Setting | Default | Purpose |
|---------|---------|---------|
| `OutboxBatchSize` | 10 | Max messages processed per job run |
| `OutboxMaxRetries` | 10 | Max delivery attempts before marking Failed |
| `OutboxRetentionDays` | 150 | Days to keep sent/failed messages before cleanup |

## Admin Dashboard

Route: `/Admin/EmailOutbox` — requires Admin role.

Features:
- Stats: queued count, sent in last 24h, failed count
- Global pause/resume toggle (stored in `SystemSettings` as `email_outbox_paused`)
- Message table: recent messages with status, recipient, subject, retry count, last error
- Per-message retry button (resets a Failed message back to Queued)

When paused, `ProcessEmailOutboxJob` skips processing without dequeuing messages.

## Global Pause

The `email_outbox_paused` key in `system_settings` controls whether the outbox processor runs. Pause/Resume actions on the dashboard update this setting. Useful during maintenance windows or when diagnosing delivery issues.

## Metrics (OpenTelemetry)

| Metric | Type | Description |
|--------|------|-------------|
| `humans.email_queued_total` | Counter | Emails added to the outbox |
| `humans.emails_sent_total` | Counter | Emails successfully delivered |
| `humans.email_failed_total` | Counter | Emails that exhausted all retries |
| `humans.email_outbox_pending` | ObservableGauge | Current queued message count |
