<!-- freshness:triggers
  src/Humans.Application/Services/Email/**
  src/Humans.Domain/Entities/EmailOutboxMessage.cs
  src/Humans.Infrastructure/Data/Configurations/Email/**
  src/Humans.Infrastructure/Repositories/EmailOutboxRepository.cs
  src/Humans.Web/Controllers/EmailController.cs
-->
<!-- freshness:flag-on-change
  Outbox queue/retry semantics, pause flag ownership, and SDK-free composer/processor split — review when Email service/repository/entity change.
-->

# Email — Section Invariants

Transactional email outbox: queue, render, deliver, retry, pause/resume. Backs campaign sends, onboarding welcome, shift notifications, feedback replies.

## Concepts

- An **Outbox Message** is a single queued email record with recipient, subject, rendered HTML body, status, retry metadata, and optional links to `User` / `CampaignGrant` / `ShiftSignup`.
- The **Outbox Pause Flag** is a `SystemSetting` key (`email_outbox_paused`) that, when `"true"`, causes `ProcessEmailOutboxJob` to skip all delivery attempts on its next tick. Resuming flips it back to `"false"`.
- **Email Body Composition** is Infrastructure-free — templates render inside the Application-layer `IEmailBodyComposer` so business code can build messages without pulling MailKit.
- **Delivery** is `IImmediateOutboxProcessor` (Infrastructure), which handles SMTP via MailKit or via the stub transport in dev.

## Data Model

### EmailOutboxMessage

**Table:** `email_outbox_messages`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| RecipientEmail | string | Delivery address |
| RecipientName | string? | Display name |
| Subject | string | Email subject line |
| HtmlBody | string | Rendered HTML body |
| PlainTextBody | string? | Optional plain-text alternative |
| TemplateName | string | Template identifier used to render this message |
| UserId | Guid? | FK → User (optional) — **FK only**, no nav |
| CampaignGrantId | Guid? | FK → CampaignGrant (optional) — **FK only**, no nav |
| ShiftSignupId | Guid? | FK → ShiftSignup (optional) — **FK only**, no nav |
| ReplyTo | string? | Reply-To header value |
| ExtraHeaders | string? | JSON-encoded additional headers (e.g., `List-Unsubscribe`) |
| Status | EmailOutboxStatus | Queued / Sent / Failed |
| CreatedAt | Instant | When queued |
| PickedUpAt | Instant? | When first picked up by the job |
| SentAt | Instant? | When successfully delivered |
| RetryCount | int | Number of delivery attempts |
| LastError | string? | Last delivery error message |
| NextRetryAt | Instant? | Earliest time for next retry attempt |

**Indexes:** `(Status, NextRetryAt)` for the delivery job's scan.

### EmailOutboxStatus

| Value | Description |
|-------|-------------|
| Queued | Awaiting delivery |
| Sent | Successfully delivered |
| Failed | Exhausted all retries |

Stored as int.

### SystemSetting key owned by this section

| Key | Purpose |
|-----|---------|
| `email_outbox_paused` | When `"true"`, `ProcessEmailOutboxJob` skips processing. Read / written through `IEmailOutboxService.IsPausedAsync` / `SetPausedAsync`. |

Per design-rules §8, each `system_settings` key is owned by the consuming section's repository. Email owns this key; do not touch it from any other section.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any service / job | Queue a message via `IEmailOutboxService.EnqueueAsync(...)` or a typed send method (e.g. `SendWelcomeAsync`, `SendShiftStatusAsync`) |
| Admin | Pause / resume outbox. Retry a failed message (re-queue). Discard a failed message (delete). View the outbox dashboard. Preview rendered templates |
| Any authenticated human | View own outbox (`/Profile/Me/Outbox`) — emails addressed to them |
| HumanAdmin, Board, Admin | View another human's outbox (`/Profile/{id}/Admin/Outbox`) |

## Invariants

- Every outgoing email writes a row to `email_outbox_messages` before any transport attempt. No "fire-and-forget" paths that bypass the table — this is the audit trail for delivery.
- `ProcessEmailOutboxJob` (Hangfire recurring) picks up `Queued` or `Failed`-with-retry-budget rows and attempts delivery. Each attempt updates `Status`, `PickedUpAt`, `SentAt`, `RetryCount`, `LastError`, `NextRetryAt`.
- While `email_outbox_paused = "true"`, the job returns immediately — no rows are picked up.
- Failed messages with `RetryCount >= MaxRetries` stay `Status = Failed` and stop retrying. Admin can manually re-queue (reset `RetryCount = 0`, `Status = Queued`, `NextRetryAt = now`) or discard.
- `IEmailBodyComposer` lives in Application; `IImmediateOutboxProcessor` lives in Infrastructure. The composer is SDK-free so Application-layer services can build messages without pulling MailKit.
- `EmailOutboxService` implements `IUserDataContributor` (design-rules §8a) — the GDPR export includes a user's outgoing-message slice.

## Negative Access Rules

- Regular humans **cannot** view another human's outbox.
- Services **cannot** send email by calling MailKit / SmtpClient directly — route through `IEmailOutboxService.EnqueueAsync` or a typed send method.
- The pause flag **cannot** be read or written by any non-Email service — other sections must not touch `system_settings` with key `email_outbox_paused`.
- Outbox rows **cannot** be deleted except by `CleanupEmailOutboxJob` (retention-based) or admin discard. No service clears rows as a side-effect.

## Triggers

- **On enqueue:** row inserted with `Status = Queued`, `CreatedAt = now`, `RetryCount = 0`, `NextRetryAt = now`.
- **On successful delivery:** `Status = Sent`, `SentAt = now`. No retry slot consumed.
- **On transient failure (5xx, timeout, 4xx except rejection):** `Status = Failed`, `RetryCount += 1`, `NextRetryAt = now + backoff`, `LastError = message`. Next tick re-attempts if retry budget allows.
- **On permanent rejection (hard 5xx / bad address):** `Status = Failed`, `RetryCount = MaxRetries`, `NextRetryAt = null`. No further retries.
- **On admin pause:** `SystemSetting email_outbox_paused = "true"`. Audit entry written.
- **On admin resume:** `SystemSetting email_outbox_paused = "false"`. Audit entry written.
- **On admin retry of failed message:** row reset (`Status = Queued`, `RetryCount = 0`, `NextRetryAt = now`). Audit entry written.

## Cross-Section Dependencies

- **Profiles:** `IUserEmailService.GetNotificationTargetEmailsAsync` — resolves the effective notification email for a user id (used by typed send methods).
- **Campaigns:** `ICampaignService` queues campaign wave messages via this section; per-grant latest-status is mirrored to `CampaignGrant.LatestEmailStatus` / `LatestEmailAt`.
- **Shifts:** `IShiftSignupService` sends approve/refuse/voluntell emails through this section.
- **Feedback:** `IFeedbackService` sends admin-reply emails through this section.
- **Onboarding:** `IOnboardingService` sends welcome emails through this section on Volunteer activation.

## Architecture

**Owning services:** `EmailOutboxService`, `OutboxEmailService`, `EmailService`
**Owned tables:** `email_outbox_messages`
**Owned SystemSetting keys:** `email_outbox_paused`
**Status:** (A) Migrated (peterdrier/Humans PR for issue nobodies-collective/Humans#548, 2026-04-22).

- `EmailOutboxService` and `OutboxEmailService` live in `Humans.Application.Services.Email/` and depend only on Application-layer abstractions.
- `IEmailOutboxRepository` (impl `Humans.Infrastructure/Repositories/EmailOutboxRepository.cs`) is the only file that touches `email_outbox_messages` via `DbContext`. Also owns the reads/writes against `system_settings` for the pause flag.
- **Decorator decision — no caching decorator.** Outbox is a sequential queue drain, not a hot-path read shape.
- **Cross-domain nav stripped:** `EmailOutboxMessage.User`. User display data (when needed for admin views) resolves via `IUserService.GetByIdsAsync`.
- **Two new connectors keep Infrastructure concerns out of Application:**
  - `IEmailBodyComposer` (Application) — renders a message into `HtmlBody`/`PlainTextBody` given a template name + model. SDK-free.
  - `IImmediateOutboxProcessor` (Infrastructure) — drives MailKit/SMTP (`HangfireImmediateOutboxProcessor` in prod; `StubEmailTransport` in dev). Never referenced from Application.
- **GDPR:** `EmailOutboxService` implements `IUserDataContributor`.

### Touch-and-clean guidance

- Do **not** call MailKit / `SmtpClient` / `SmtpEmailTransport` directly from business code. Route through `IEmailOutboxService.EnqueueAsync` or a typed send method.
- Do **not** read or write the `email_outbox_paused` `SystemSetting` key from outside this section.
- New typed send methods (`SendXxxAsync`) go on `IEmailOutboxService` (or a section-owned facade if the template needs section-specific composition logic). Prefer extending `IEmailOutboxService` over adding a new connector.
- New headers (e.g., `List-Unsubscribe`) go in `ExtraHeaders` as JSON — do not add new columns per-header. The outbox schema is stable.
