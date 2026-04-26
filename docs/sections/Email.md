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
- The **Outbox Pause Flag** is a `SystemSetting` row keyed `IsEmailSendingPaused` that, when `"true"`, causes `ProcessEmailOutboxJob` to skip all delivery attempts on its next tick. Resuming flips it back to `"false"`.
- **Email Body Composition** is Infrastructure-free at the consumer boundary — `IEmailBodyComposer` is an Application-layer abstraction so business code can wrap a rendered HTML body without pulling MailKit. The current implementation (`BrandedEmailBodyComposer`) lives in Infrastructure.
- **Delivery** is performed by `ProcessEmailOutboxJob` (Infrastructure) via `IEmailTransport` (`SmtpEmailTransport` in prod, `StubEmailTransport` in dev/test). `IImmediateOutboxProcessor` (`HangfireImmediateOutboxProcessor`) is the trigger for time-sensitive templates that need to fire the next job run immediately rather than wait for the recurring tick.
- **Two `IEmailService` implementations exist:** `OutboxEmailService` (Application, default — writes to the outbox) and `SmtpEmailService` (Infrastructure, legacy — sends inline via SMTP). DI binds `IEmailService` to `OutboxEmailService`; `SmtpEmailService` is no longer wired in.

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

**Indexes:**
- `(SentAt, RetryCount, NextRetryAt, PickedUpAt)` — composite index for the processor's scan (`SentAt IS NULL AND RetryCount < max AND (NextRetryAt IS NULL OR NextRetryAt <= now) AND (PickedUpAt IS NULL OR PickedUpAt < staleThreshold)`).
- `UserId` — per-human outbox views.
- `CampaignGrantId` — campaign grant tracking.
- `(ShiftSignupId, TemplateName)` — filtered (`ShiftSignupId IS NOT NULL`) for shift-notification dedup.

### EmailOutboxStatus

| Value | Description |
|-------|-------------|
| Queued | Awaiting delivery |
| Sent | Successfully delivered |
| Failed | Last attempt failed; may still retry until `RetryCount` reaches `OutboxMaxRetries` |

Stored as **string** (`HasConversion<string>()`, `HasMaxLength(20)`). The `Failed` status is a single bucket — there is no separate "permanently failed" status; whether a `Failed` row will be retried is determined by `RetryCount < OutboxMaxRetries` and `NextRetryAt`. There is also no `Sending` status — in-flight rows are tracked by `PickedUpAt` rather than a status transition.

### SystemSetting key owned by this section

| Key | Purpose |
|-----|---------|
| `IsEmailSendingPaused` | When `"true"`, `ProcessEmailOutboxJob` skips processing. Read / written through `IEmailOutboxService.IsEmailPausedAsync` / `SetEmailPausedAsync` (which delegate to `IEmailOutboxRepository.GetSendingPausedAsync` / `SetSendingPausedAsync`). The job itself also reads it directly through the repository, since the job is registered in Infrastructure. |

Per design-rules §8, each `system_settings` key is owned by the consuming section's repository. Email owns this key; do not touch it from any other section.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any service / job | Queue a message via a typed `IEmailService.Send…Async` method (e.g. `SendWelcomeEmailAsync`, `SendApplicationApprovedAsync`, `SendCampaignCodeAsync`). The default `IEmailService` is `OutboxEmailService`, which writes the row to `email_outbox_messages`. |
| Admin (`AdminOnly` policy) | Pause / resume outbox. Retry a failed message (re-queue). Discard a failed message (delete). View the outbox dashboard at `/Email/EmailOutbox`. Preview rendered templates at `/Email/EmailPreview`. |
| Any authenticated human | View own outbox (`GET /Profile/Me/Outbox`) — emails where `UserId` matches the signed-in user. |
| HumanAdmin, Board, Admin (`HumanAdminBoardOrAdmin` policy) | View another human's outbox (`GET /Profile/{id}/Admin/Outbox`). |

## Invariants

- Every outgoing email queued through `OutboxEmailService` writes a row to `email_outbox_messages` before any transport attempt. No "fire-and-forget" paths through `IEmailService` bypass the table — this is the audit trail for delivery.
- `ProcessEmailOutboxJob` (Hangfire recurring, every minute — `*/1 * * * *`) selects rows with `SentAt IS NULL`, `RetryCount < OutboxMaxRetries`, `NextRetryAt <= now` (or null), and `PickedUpAt < now − 5 min` (or null). The batch is bounded by `OutboxBatchSize`. Selected rows are stamped `PickedUpAt = now` to block concurrent runs from picking the same rows, then sent one at a time through `IEmailTransport.SendAsync` with a 1-second throttle delay between successful sends.
- While `IsEmailSendingPaused = "true"`, the job returns immediately — no rows are picked up.
- On success the row becomes `Status = Sent`, `SentAt = now`, `PickedUpAt = null`. On failure the row becomes `Status = Failed`, `RetryCount += 1`, `LastError = ex.Message` (truncated to 4000 chars), `NextRetryAt = now + 2^(RetryCount+1) minutes`, `PickedUpAt = null`. Failed rows with `RetryCount >= OutboxMaxRetries` stop being picked up by future scans (they remain `Status = Failed` forever unless an admin retries or discards them). The job does not distinguish hard vs soft transport failures — every thrown exception increments the retry counter.
- Admin retry resets a row to `Status = Queued`, `RetryCount = 0`, `LastError = null`, `NextRetryAt = null`, `PickedUpAt = null`.
- Recipient addresses ending in `@localhost` or `@ticketstub.local` are short-circuit-marked `Sent` without contacting the transport (test addresses; sending real mail to them would damage sender reputation).
- `IEmailBodyComposer` is an Application-layer abstraction so consumers stay SDK-free; the implementation (`BrandedEmailBodyComposer`) and `IImmediateOutboxProcessor` (`HangfireImmediateOutboxProcessor`) live in Infrastructure.
- The Email section does **not** contribute to the GDPR export (`IUserDataContributor`). User-scoped outbox history is exposed only through the `/Profile/Me/Outbox` and `/Profile/{id}/Admin/Outbox` views.

## Negative Access Rules

- Regular humans **cannot** view another human's outbox.
- Services **cannot** send email by calling MailKit / `SmtpClient` / `IEmailTransport` directly — route through a typed `IEmailService.Send…Async` method (which writes to the outbox).
- The pause flag **cannot** be read or written by any non-Email code — other sections must not touch `system_settings` with key `IsEmailSendingPaused`. The processor job is the only Infrastructure-side reader and it goes through `IEmailOutboxRepository`.
- Outbox rows **cannot** be deleted except by `CleanupEmailOutboxJob` (retention-based) or admin discard. No service clears rows as a side-effect.

## Triggers

- **On enqueue (`OutboxEmailService`):** row inserted with `Status = Queued`, `CreatedAt = now`, `RetryCount = 0`, `NextRetryAt = null`, `PickedUpAt = null`. For categories that are opt-outable, the row is suppressed entirely if the recipient has opted out; otherwise unsubscribe headers (`List-Unsubscribe`, `List-Unsubscribe-Post`) are serialised into `ExtraHeaders` and a footer link is wrapped into the body.
- **On time-sensitive enqueue** (`email_verification`, `magic_link_login`, `magic_link_signup`, `workspace_credentials`): after the row is added, `IImmediateOutboxProcessor.TriggerImmediate()` is called to run the processor without waiting for the next minute tick.
- **On batch pick-up:** rows in the batch are stamped `PickedUpAt = now` (block window 5 minutes).
- **On successful delivery:** `Status = Sent`, `SentAt = now`, `PickedUpAt = null`. If `CampaignGrantId` is set, `ICampaignService.UpdateGrantEmailStatusAsync(grantId, Sent, now)` mirrors the status onto the grant. The job then sleeps 1 second before processing the next message.
- **On delivery failure (any thrown exception):** `Status = Failed`, `RetryCount += 1`, `LastError = ex.Message` (truncated to 4000 chars), `NextRetryAt = now + 2^(RetryCount+1) minutes`, `PickedUpAt = null`. If `CampaignGrantId` is set, the grant is mirrored with `Failed`. Once `RetryCount >= OutboxMaxRetries`, the processor query stops returning the row.
- **On admin pause:** `SystemSetting IsEmailSendingPaused = "true"`. (No audit entry is written by the controller today.)
- **On admin resume:** `SystemSetting IsEmailSendingPaused = "false"`. (No audit entry is written by the controller today.)
- **On admin retry of failed message:** row reset to `Status = Queued`, `RetryCount = 0`, `LastError = null`, `NextRetryAt = null`, `PickedUpAt = null`. (No audit entry today.)
- **On admin discard:** row is deleted from `email_outbox_messages`.
- **`CleanupEmailOutboxJob` (Hangfire recurring, weekly Sunday 03:00 UTC — `0 3 * * 0`):** deletes `Status = Sent` rows whose `SentAt` is older than `OutboxRetentionDays`.

## Cross-Section Dependencies

- **Profiles:** `IUserEmailService.GetNotificationTargetEmailsAsync` — resolves the effective notification email for a user id (used by typed send methods).
- **Campaigns:** `ICampaignService` queues campaign wave messages via this section; per-grant latest-status is mirrored to `CampaignGrant.LatestEmailStatus` / `LatestEmailAt`.
- **Shifts:** `IShiftSignupService` sends approve/refuse/voluntell emails through this section.
- **Feedback:** `IFeedbackService` sends admin-reply emails through this section.
- **Onboarding:** `IOnboardingService` sends welcome emails through this section on Volunteer activation.

## Architecture

**Owning services:** `OutboxEmailService` (`IEmailService`), `EmailOutboxService` (`IEmailOutboxService`)
**Owned tables:** `email_outbox_messages`
**Owned SystemSetting keys:** `IsEmailSendingPaused`
**Status:** (A) Migrated.

- `EmailOutboxService` and `OutboxEmailService` live in `Humans.Application.Services.Email/` and depend only on Application-layer abstractions.
- `IEmailOutboxRepository` (impl `src/Humans.Infrastructure/Repositories/Email/EmailOutboxRepository.cs`) is the only file that touches `DbContext.EmailOutboxMessages`. It also owns the single `IsEmailSendingPaused` row in `system_settings`. Registered Singleton via `IDbContextFactory<HumansDbContext>` so it can be injected into Application services and the recurring job alike.
- **Decorator decision — no caching decorator.** Outbox is a sequential queue drain, not a hot-path read shape.
- **Cross-domain nav stripped:** `EmailOutboxMessage.User`. User display data (when needed for admin views) resolves via `IUserService`. The FK column + `ON DELETE SET NULL` cascade is preserved through a shadow relationship in `EmailOutboxMessageConfiguration`.
- **Two connectors keep Infrastructure concerns out of Application:**
  - `IEmailBodyComposer` (Application interface) — wraps a rendered HTML body into the branded shell and produces the plain-text alternative. Implementation `BrandedEmailBodyComposer` lives in Infrastructure.
  - `IImmediateOutboxProcessor` (Application interface) — triggers an out-of-band processor run for time-sensitive templates. Implementation `HangfireImmediateOutboxProcessor` lives in Infrastructure.
- **Transport:** `IEmailTransport` is bound to `SmtpEmailTransport` when `Email:SmtpHost` is configured (required in Production), otherwise `StubEmailTransport` (dev/test). The job uses the transport directly; Application code never sees it.
- **Legacy:** `SmtpEmailService` (Infrastructure `IEmailService`) is the pre-outbox inline-send implementation. It is no longer registered in DI but remains in the codebase.

### Touch-and-clean guidance

- Do **not** call MailKit / `SmtpClient` / `IEmailTransport` directly from business code. Route through a typed `IEmailService.Send…Async` method.
- Do **not** read or write the `IsEmailSendingPaused` `SystemSetting` key from outside this section.
- New typed send methods (`Send…Async`) go on `IEmailService` and are implemented in `OutboxEmailService` (which calls `IEmailRenderer` + `IEmailBodyComposer` + `IEmailOutboxRepository.AddAsync`).
- New headers (e.g., `List-Unsubscribe`) go in `ExtraHeaders` as JSON — do not add new columns per-header. The outbox schema is stable.
