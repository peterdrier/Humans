<!-- freshness:triggers
  src/Sections/Humans.Email/**
  src/Sections/Humans.Email.Contracts/**
-->
<!-- freshness:flag-on-change
  Outbox queue/retry semantics, pause flag ownership, and SDK-free composer/processor split — review when Email service/repository/entity change.
-->

# Email — Section Invariants

Transactional email outbox: queue, render, deliver, retry, pause/resume. Backs campaign sends, onboarding welcome, shift notifications, feedback replies.

## Concepts

- An **Outbox Message** is a single queued email record with recipient, subject, rendered HTML body, status, retry metadata, and optional links to `User` / `CampaignGrant` / `ShiftSignup`.
- The **Outbox Pause Flag** is a `SystemSetting` row keyed `IsEmailSendingPaused` that, when `"true"`, causes `ProcessEmailOutboxJob` to skip all delivery attempts on its next tick. Resuming flips it back to `"false"`.
- **Email Body Composition** happens entirely inside the section: `IEmailBodyComposer` / `BrandedEmailBodyComposer` are internal, as are `IEmailRenderer` / `EmailRenderer` and the two transports. Business code outside the section builds an `EmailMessage` through `IEmailMessageFactory` and hands it to `IEmailService`. Authorized UI previews can pass an always-send system message to the read-only `IEmailPreviewServiceRead`; it returns the exact canonical branded wrapper without enqueueing it.
- **Delivery** is performed by `EmailOutboxProcessor` (section) via `IEmailTransport` (`SmtpEmailTransport` in prod, `StubEmailTransport` in dev/test). `ProcessEmailOutboxJob` (`Humans.Email/Jobs/`) is the Hangfire scheduler shim that calls it through `IEmailOutboxProcessor`. `IImmediateOutboxProcessor` (`HangfireImmediateOutboxProcessor`, `Humans.Email/Contracts/`) is the trigger for time-sensitive templates that need to fire the next job run immediately rather than wait for the recurring tick.
- **One `IEmailService` implementation exists:** `OutboxEmailService` (Application, default — writes to the outbox). DI binds `IEmailService` to `OutboxEmailService`.

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
| UserId | Guid? | Bare cross-section id (optional) — no FK constraint, no nav |
| CampaignGrantId | Guid? | Bare cross-section id (CampaignGrant, Campaigns) — no FK constraint, no nav; status mirroring writes through `ICampaignService` |
| ShiftSignupId | Guid? | Bare cross-section id (ShiftSignup, Shifts) — no FK constraint, no nav; dedup query filters on the column directly |
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
| `IsEmailSendingPaused` | When `"true"`, `ProcessEmailOutboxJob` skips processing. Read / written through `IEmailOutboxService.IsEmailPausedAsync` / `SetEmailPausedAsync`, which delegate to `ISettingsService` (the Settings section owns the `system_settings` table). The processor job also reads it through `IEmailOutboxService.IsEmailPausedAsync`. |

Per design-rules §8, each `system_settings` key is owned by its consuming section. Email owns this key (its semantics and the only reads/writes); persistence routes through `ISettingsService`. Do not touch this key from any other section.

## Routing

| Route | Auth | Controller action |
|-------|------|-------------------|
| `GET /Email/EmailOutbox` | `AdminOnly` | `EmailController.EmailOutbox` — outbox dashboard |
| `POST /Email/EmailOutbox/Pause` | `AdminOnly` | `EmailController.PauseEmailSending` |
| `POST /Email/EmailOutbox/Resume` | `AdminOnly` | `EmailController.ResumeEmailSending` |
| `POST /Email/EmailOutbox/Retry/{id}` | `AdminOnly` | `EmailController.RetryEmailOutboxMessage` |
| `POST /Email/EmailOutbox/Discard/{id}` | `AdminOnly` | `EmailController.DiscardEmailOutboxMessage` |
| `GET /Email/EmailPreview` | `AdminOnly` | `EmailController.EmailPreview` — rendered template gallery |
| `GET /Profile/Me/Outbox` | authenticated | `ProfileController` — own outbox history |
| `GET /Users/Admin/{id}/Outbox` | `HumanAdminBoardOrAdmin` | `UsersAdminController` — another user's outbox history |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any service / job | Build a fully-rendered `EmailMessage` via a typed `IEmailMessageFactory` method (e.g. `AccessSuspended`, `ApplicationApproved`, `CampaignCode`) and hand it to the single `IEmailService.SendAsync(message, ct)`. The default `IEmailService` is `OutboxEmailService`, which writes the row to `email_outbox_messages`. |
| Admin (`AdminOnly` policy) | Pause / resume outbox. Retry a failed message (re-queue). Discard a failed message (delete). View the outbox dashboard at `/Email/EmailOutbox`. Preview rendered templates at `/Email/EmailPreview`. |
| Any authenticated human | View own outbox (`GET /Profile/Me/Outbox`) — emails where `UserId` matches the signed-in user. |
| HumanAdmin, Board, Admin (`HumanAdminBoardOrAdmin` policy) | View another human's outbox (`GET /Users/Admin/{id}/Outbox`). |

## Invariants

- Every outgoing email queued through `OutboxEmailService` writes a row to `email_outbox_messages` before any transport attempt — the audit trail for delivery. The single exception is `EmailMessage.DoNotPersist`, which hands the message straight to `IEmailTransport` and writes no row; it is set by exactly one template, `account_deleted`, because its recipient is a human the Article 17 cascade has just erased and a row would re-create their address, name and body. Such a message is never retried: retrying would mean keeping the address in order to retry with it.
- `ProcessEmailOutboxJob` (Hangfire recurring, every minute — `*/1 * * * *`) selects rows with `SentAt IS NULL`, `RetryCount < OutboxMaxRetries`, `NextRetryAt <= now` (or null), and `PickedUpAt < now − 5 min` (or null). The batch is bounded by `OutboxBatchSize` and ordered **time-sensitive templates first, then FIFO by `CreatedAt` within each class** — see the priority invariant below. Selected rows are stamped `PickedUpAt = now` to block concurrent runs from picking the same rows, then sent one at a time through `IEmailTransport.SendAsync` with a 1-second throttle delay between successful sends.
- While `IsEmailSendingPaused = "true"`, the job returns immediately — no rows are picked up.
- **Time-sensitive mail jumps the queue.** `GetProcessingBatchAsync` orders rows whose `TemplateName` is one of `email_verification`, `magic_link_login`, `magic_link_signup`, `workspace_credentials` (`TimeSensitiveTemplates.Names`) ahead of every other row, then by `CreatedAt` within each of the two classes. Without this, a magic-link login lands behind whatever bulk mail is already queued and drains at the 1 send/second throttle — the user is locked out until the backlog clears (nobodies-collective/Humans#1122). No column and no schema change back this: it is ordering only, computed DB-side as a `CASE` in the `ORDER BY`. The same list drives `TriggerImmediate` on enqueue, so the two behaviours cannot drift.
- On success the row becomes `Status = Sent`, `SentAt = now`, `PickedUpAt = null`. On failure the row becomes `Status = Failed`, `RetryCount += 1`, `LastError = ex.Message` (truncated to 4000 chars), `NextRetryAt = now + 2^(RetryCount+1) minutes`, `PickedUpAt = null`. Failed rows with `RetryCount >= OutboxMaxRetries` stop being picked up by future scans (they remain `Status = Failed` forever unless an admin retries or discards them). The job does not distinguish hard vs soft transport failures — every thrown exception increments the retry counter.
- `Status = Sent` / `SentAt` records SMTP-server acceptance, **not** inbox delivery. Bounce processing is out of scope — a message marked `Sent` may still bounce silently at the recipient's mail server. Admins watching the outbox dashboard see SMTP outcomes, not inbox outcomes.
- Admin retry resets a row to `Status = Queued`, `RetryCount = 0`, `LastError = null`, `NextRetryAt = null`, `PickedUpAt = null`.
- Recipient addresses ending in `@localhost` or `@ticketstub.local` are short-circuit-marked `Sent` without contacting the transport (test addresses; sending real mail to them would damage sender reputation).
- `IEmailBodyComposer` is a section-internal abstraction so `OutboxEmailService` stays free of `IHostEnvironment`/configuration dependencies; the implementation (`BrandedEmailBodyComposer`) is section-internal too. `IImmediateOutboxProcessor` (`HangfireImmediateOutboxProcessor`) lives in `Humans.Email/Contracts/`.
- `IEmailPreviewServiceRead` is the only cross-section seam for side-effect-free final-body rendering. It delegates to the same internal `IEmailBodyComposer` as the outbox and accepts only always-send system messages; opt-outable messages require recipient-specific unsubscribe policy and are rejected.
- `EmailOutboxService` is the section's `IUserDataContributor`. Article 15 exports the human's own outbox history under the `EmailOutbox` key — the same rows `/Profile/Me/Outbox` already shows them. Article 17 deletes **every** row with a matching `UserId`, whatever its status: the retention sweep only reaches `Sent` rows past the cutoff, so failed and queued rows would otherwise outlive the erasure. The deletion-confirmation mail that follows the cascade leaves no row to delete (`DoNotPersist`) — it is sent after the collapse, so its `UserId` would resolve to null and put it out of this contributor's reach.

## Negative Access Rules

- Regular humans **cannot** view another human's outbox.
- Services **cannot** send email by calling MailKit / `SmtpClient` / `IEmailTransport` directly — build an `EmailMessage` via `IEmailMessageFactory` and route through `IEmailService.SendAsync`, which owns the outbox-versus-direct decision.
- The pause flag **cannot** be read or written by any non-Email code — other sections must not touch `system_settings` with key `IsEmailSendingPaused`. The processor job is the only Infrastructure-side reader and it goes through `IEmailOutboxService.IsEmailPausedAsync`.
- Outbox rows **cannot** be deleted except by `CleanupEmailOutboxJob` (retention-based), admin discard, or `EmailOutboxService.EraseForUserAsync` (GDPR Article 17). No service clears rows as a side-effect.

## Triggers

- **On enqueue (`OutboxEmailService`):** row inserted with `Status = Queued`, `CreatedAt = now`, `RetryCount = 0`, `NextRetryAt = null`, `PickedUpAt = null`. For categories that are opt-outable, the row is suppressed entirely if the recipient has opted out; otherwise unsubscribe headers (`List-Unsubscribe`, `List-Unsubscribe-Post`) are serialised into `ExtraHeaders` and a footer link is wrapped into the body.
- **On time-sensitive enqueue** (`email_verification`, `magic_link_login`, `magic_link_signup`, `workspace_credentials` — the names on `TimeSensitiveTemplates`): after the row is added, `IImmediateOutboxProcessor.TriggerImmediate()` is called to run the processor without waiting for the next minute tick. That run also picks the row up first, ahead of any queued bulk mail.
- **On batch pick-up:** rows in the batch are stamped `PickedUpAt = now` (block window 5 minutes).
- **On successful delivery:** `Status = Sent`, `SentAt = now`, `PickedUpAt = null`. If `CampaignGrantId` is set, `ICampaignService.UpdateGrantEmailStatusAsync(grantId, Sent, now)` mirrors the status onto the grant. The job then sleeps 1 second before processing the next message.
- **On delivery failure (any thrown exception):** `Status = Failed`, `RetryCount += 1`, `LastError = ex.Message` (truncated to 4000 chars), `NextRetryAt = now + 2^(RetryCount+1) minutes`, `PickedUpAt = null`. If `CampaignGrantId` is set, the grant is mirrored with `Failed`. Once `RetryCount >= OutboxMaxRetries`, the processor query stops returning the row.
- **On admin pause:** `SystemSetting IsEmailSendingPaused = "true"`. (No audit entry is written by the controller today.)
- **On admin resume:** `SystemSetting IsEmailSendingPaused = "false"`. (No audit entry is written by the controller today.)
- **On admin retry of failed message:** row reset to `Status = Queued`, `RetryCount = 0`, `LastError = null`, `NextRetryAt = null`, `PickedUpAt = null`. (No audit entry today.)
- **On admin discard:** row is deleted from `email_outbox_messages`.
- **`CleanupEmailOutboxJob` (Hangfire recurring, weekly Sunday 03:00 UTC — `0 3 * * 0`):** deletes `Status = Sent` rows whose `SentAt` is older than `OutboxRetentionDays`.

## Cross-Section Dependencies

- **Profiles:** `IUserEmailService.GetUserIdByVerifiedEmailAsync` — resolves `UserId` from a recipient address so `OutboxEmailService` can link outbox rows to users. `ICommunicationPreferenceService` — checked by `OutboxEmailService` for per-category opt-outs and to generate `List-Unsubscribe` headers.
- **Campaigns:** `ICampaignService` queues campaign wave messages via this section; per-grant latest-status is mirrored to `CampaignGrant.LatestEmailStatus` / `LatestEmailAt`.
- **Shifts:** `IShiftSignupService` sends approve/refuse/voluntell emails through this section.
- **Feedback:** `IFeedbackService` sends admin-reply emails through this section.
- **Onboarding:** `IOnboardingService` sends welcome emails through this section on Volunteer activation.
- **Settings:** `ISettingsService` — `EmailOutboxService` reads / writes the `IsEmailSendingPaused` key in `system_settings` through this service (the Settings section owns the table).

## Architecture

**Owning services:** `OutboxEmailService` (`IEmailService`), `EmailOutboxService` (`IEmailOutboxService`)
**Owned tables:** `email_outbox_messages`
**Owned SystemSetting keys:** `IsEmailSendingPaused`
**Status:** (A) Migrated.

- The section lives at `src/Sections/Humans.Email` with its cross-section surface on the `Humans.Email.Contracts` leaf project (nobodies-collective/Humans#866, G5). Everything else — the entity, the repository, the renderer, the body composer, the SMTP transport, the outbox admin surface — is `internal`.
- `IEmailOutboxRepository` (impl `src/Sections/Humans.Email/Data/EmailOutboxRepository.cs`) is the only file that touches `DbContext.EmailOutboxMessages`. `TimeSensitiveTemplates` (`Domain/`) holds the four template names that both the factory (`TriggerImmediate`) and the repository (batch priority ordering) key off. The `IsEmailSendingPaused` row in `system_settings` is no longer read or written here — `EmailOutboxService` reaches it through `ISettingsService` (Settings section owns the table). Registered Singleton via `IDbContextFactory<EmailDbContext>` (peeled out of `HumansDbContext` in #858) so it can be injected into Application services and the recurring job alike.
- **Decorator decision — no caching decorator.** Outbox is a sequential queue drain, not a hot-path read shape.
- **Cross-domain navs stripped:** `EmailOutboxMessage` carries no navigation properties at all — `UserId`, `CampaignGrantId`, and `ShiftSignupId` are bare Guid columns in `EmailOutboxMessageConfiguration` with no FK constraint and no nav (#992 cut the FK, #996 cut the last navs). A stale id is an accepted orphan on this append-only send log, pruned on age by `DeleteSentOlderThanAsync`. User display data resolves via `IUserService`; grant status mirroring goes through `ICampaignService`; the shift-signup dedup query filters on `ShiftSignupId` directly.
- **`Humans.Email.Contracts` — everything consumed from outside the section:**
  - `IEmailService` + `EmailMessage` — the one transport entry point, called by nine `Humans.Application` services, six `Humans.Infrastructure` jobs and six moved sections.
  - `IEmailMessageFactory` — the typed builders those callers use to construct an `EmailMessage`.
    `SurveyInvitation` accepts optional plain-text custom subject/message values from Surveys; the
    internal renderer trims and safely encodes them while retaining the existing template, generated
    answer link, System category, and localized standard-copy fallback.
  - `IEmailPreviewServiceRead` + `RenderedEmailPreview` — read-only final-body rendering for
    authorized cross-section preview pages. It applies the same internal branded composer as the
    outbox, creates no outbox row, and deliberately rejects opt-outable categories whose exact
    footer depends on recipient-specific send policy.
  - `IEmailOutboxServiceRead` + `EmailOutboxMessageDto` — per-human outbox history for Shell's `/Profile/Me/Outbox` and `/Users/Admin/{id}/Outbox`.
  - `IEmailOutboxProcessor` / `IEmailOutboxRetention` — what `ProcessEmailOutboxJob` / `CleanupEmailOutboxJob` drive. Both jobs moved out of Base into the section at G5 lane 5b-1 (initially under `Contracts/`, then into their own `Humans.Email/Jobs/` folder at nobodies-collective/Humans#1353's Jobs/ carve-out), so these two have no consumer outside the section any more and could move inward in a later pass.
  - `IImmediateOutboxProcessor` — was on the leaf for the opposite reason: Base *implemented* it. `HangfireImmediateOutboxProcessor` followed the job out of Base at the same lane and still lives under `Humans.Email/Contracts/` — the #1353 Jobs/ carve-out is for `IRecurringJob` implementors and `*Job`-named Hangfire jobs, and this type is neither — so both sides are now section-side.
- **Section-internal abstractions** (`Humans.Email.Services`): `IEmailOutboxService` (admin surface, consumed only by `EmailController`), `IEmailRenderer` / `EmailRenderer`, `IEmailBodyComposer` / `BrandedEmailBodyComposer`, `IEmailTransport` / `SmtpEmailTransport` / `StubEmailTransport`.
- **`EmailSettings` stays in `Humans.Base.Configuration` and is bound in Shell**, not in `Section.Register`: Auth's `MagicLinkUrlBuilder`, Profiles' `UnsubscribeTokenProvider`, `SendReConsentReminderJob` and Email's own `SmtpHealthCheck` all read it. It is Base configuration the section is merely named after.
- **`EmailOutboxStatus` stays in `Humans.Base.Enums`.** Campaigns' `CampaignGrant.LatestEmailStatus` and Surveys' `SurveyInvitation` persist it on their own tables, so it is shared Base vocabulary rather than section-internal; its `Enum_EmailOutboxStatus_*` resource keys stay in `SharedResource` with it.
- **Resource set:** the 71 `Email_*` keys moved into `Humans.Email/EmailResource.{resx,es,ca,de,fr,it}` with `EmailRenderer`, their one and only renderer. They are *not* this section's page copy — the two admin views carry no localized string at all.
- **Architecture test:** `tests/Humans.Email.Tests/EmailArchitectureTests.cs` pins the `OutboxEmailService` constructor shape, which side of the boundary each connector abstraction sits on, the one-method `IEmailService` surface, and that no section type localizes through anything but `EmailResource`. `tests/Humans.Integration.Tests/Controllers/EmailPageRenderTests.cs` is the step-12 render guard.

### Touch-and-clean guidance

- Do **not** call MailKit / `SmtpClient` / `IEmailTransport` directly from business code. Build an `EmailMessage` via `IEmailMessageFactory` and route through `IEmailService.SendAsync`.
- Do **not** read or write the `IsEmailSendingPaused` `SystemSetting` key from outside this section.
- New message types add a typed builder method on `IEmailMessageFactory` (impl `EmailMessageFactory`, which calls `IEmailRenderer` and stamps routing policy) — not a new method on `IEmailService`. The single `IEmailService.SendAsync` (impl `OutboxEmailService`, which calls `IEmailBodyComposer` + `IEmailOutboxRepository.AddAsync`) is the one shared transport path.
- New headers (e.g., `List-Unsubscribe`) go in `ExtraHeaders` as JSON — do not add new columns per-header. The outbox schema is stable.
