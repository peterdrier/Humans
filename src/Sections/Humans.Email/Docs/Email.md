<!-- freshness:triggers
  src/Sections/Humans.Email/**
  src/Sections/Humans.Email.Contracts/**
-->
<!-- freshness:flag-on-change
  Outbox queue/retry semantics, pause flag ownership, SDK-free composer/processor split, communication-preference rules (legacy fallback, cleared rows, merge fold) and unsubscribe token rules — review when Email service/repository/entity change.
-->

# Email — Section Invariants

Transactional email outbox: queue, render, deliver, retry, pause/resume. Backs campaign sends, onboarding mail, shift notifications, feedback replies. Also owns each human's per-category communication preferences and the unsubscribe surface (Users decomposition commit 14).

## Concepts

- An **Outbox Message** is a single queued email record with recipient, subject, rendered HTML body, status, retry metadata, and optional links to `User` / `CampaignGrant`.
- The **Outbox Pause Flag** is a `system_settings` row keyed `IsEmailSendingPaused` (owned by the Settings section) that, when `"true"`, causes `ProcessEmailOutboxJob` to skip all delivery attempts on its next tick. Resuming flips it back to `"false"`.
- **Email Body Composition** is the sending section's job: each section builds its own `EmailMessage` (content plus routing policy) through its internal `<Section>Emails` builder, from its own resx set, and hands it to `IEmailService` (peterdrier/Humans#1651, `memory/architecture/email-templates-live-in-sender.md`). Email renders exactly one template itself — the facilitated volunteer-to-volunteer relay, built by `IEmailMessageFactory.FacilitatedMessage` for its three callers, Email's own personal send, Camps' contact form and Teams' send-as-team (which passes the team's group as `replyTo`) — and owns the shared wrap: `IEmailBodyComposer` / `BrandedEmailBodyComposer` and the two transports are internal. Authorized UI previews can pass an always-send system message to the read-only `IEmailPreviewService`; it returns the exact canonical branded wrapper without enqueueing it. The section-internal `IEmailPreviewManagement.RenderMarkdown` is the second, generic preview seam behind the shared `_EmailComposer` component (Base), consumed only by this section's own `EmailPreviewController`: it renders arbitrary human-typed subject/Markdown through `SanitizedMarkdownRenderer` and the same branded wrapper, adding a placeholder (`#`) unsubscribe footer when the caller-supplied `MessageCategory` is opt-outable — no outbox row is created either way. The composer's **"Send to me"** button is its one sending seam: `ComposerSelfSendService` (section-internal, behind `EmailPreviewController.SendMarkdownToSelf`) queues the same sanitized body, branded by the outbox like any send, to the signed-in human's own notification address as template `composer_self_test`, `MessageCategory.System`, with a localized `[Test]` subject prefix (peterdrier/Humans#1793). The composer also shows a read-only **"To:" line** from the caller-supplied `RecipientCount` / `RecipientSummary` / `RecipientUserId`; at or above `WarnRecipientCount` (default 25) it turns into a warning and the host form's submit asks for confirmation naming the count.
- **Every human-composed send renders through `SanitizedMarkdownRenderer`** (Base) — Markdig → `Ganss.Xss.HtmlSanitizer`, one pipeline, no bespoke HTML-encode/`<br>` path. Images (`<img src>`) are restricted to `https://` — `data:`, `http:`, and `javascript:` sources are stripped via the sanitizer's `FilterUrl` hook; `<a href>` keeps the sanitizer's broader allowance. `style` attributes are stripped outright (`AllowedAttributes.Remove("style")`) so a CSS `url(...)` (e.g. `background-image`) can't smuggle a non-`https://` fetch the `<img>` rule doesn't cover. `EmailInlineStyler` (section-internal) then walks the rendered HTML with AngleSharp and stamps inline `style` attributes matching `BrandedEmailTemplate`'s palette onto h1–h3/p/a/ul/ol/li/blockquote/table/th/td/hr/code/pre, since many clients strip `<style>` blocks; it runs once, inside `BrandedEmailTemplate.Wrap`, so every branded send and preview gets it for free.
- `IEmailService.SendAsync` and `IEmailOutboxProcessor.ProcessQueuedAsync` are marked `[ExternalWrite]`: the former can send a `DoNotPersist` message directly, and the latter reaches SMTP while draining. State-changing callers must detach request cancellation at their entry point.
- Recipient log properties use Base’s email redaction policy (at most two UTF-16 units plus masked local part and domain), including queue, suppression, SMTP and stub logs.
- **Delivery** is performed by `EmailOutboxProcessor` (section) via `IEmailTransport` (`SmtpEmailTransport` when `Email:SmtpHost` is set, `StubEmailTransport` when it is empty). `ProcessEmailOutboxJob` (`Humans.Email/Jobs/`) is the Hangfire scheduler shim that calls it through `IEmailOutboxProcessor`. `IImmediateOutboxProcessor` (`HangfireImmediateOutboxProcessor`, `Humans.Email/Contracts/`) is the trigger for time-sensitive templates that need to fire the next job run immediately rather than wait for the recurring tick.
<!-- wheat: docs/plans/2026-09-16-production-readiness.md §Promotion rehearsal and recovery record -->
- Transport selection keys on the raw `Email:SmtpHost` setting (`Section.Register`), and `appsettings.json` defaults it to `smtp-relay.gmail.com`, so any host binds the real `SmtpEmailTransport` unless `Email:SmtpHost` is explicitly cleared. Removing SMTP credentials alone does not select `StubEmailTransport`.
- A persisted `EmailMessage.DeliveryId` reuses that logical delivery's outbox primary key: retries are no-ops while the row exists, even if their payload changes. Only the first enqueue records queue metrics or triggers immediate processing. Ordinary sends without a delivery id receive a new id each time. Retention/erasure removes this deduplication history; `DoNotPersist` bypasses it, and SMTP delivery retains its existing retry semantics.
- **Communication Preferences** are a human's per-category email opt-out and in-app inbox switch (`MessageCategory`; [feature spec](features/communication-preferences.md)). System and CampaignCodes are always on. `ICommunicationPreferenceService` is the cross-section read; `ICommunicationPreferenceWrite` carries the two writes MailerLite's import makes; everything else (channel-aware updates, tokens, the copy) is the internal `ICommunicationPreferenceManagement`.
- **Unsubscribe** is the no-login opt-out surface: `/Email/Unsubscribe/{token}` and the RFC 8058 `/Email/Unsubscribe/OneClick` POST. Category-aware tokens (Data Protection purpose `CommunicationPreference`, payload `{userId}|{Category}`, 90-day lifetime) redirect to `/Email/Guest/CommunicationPreferences`; legacy campaign tokens (`CampaignUnsubscribe` purpose) show the confirmation page and act on `Marketing`. Purposes and payload are unchanged from when Users served them, so tokens minted before the move still validate.
- **One `IEmailService` implementation exists:** `OutboxEmailService` (Application, default — writes to the outbox). DI binds `IEmailService` to `OutboxEmailService`.

## Data Model

### EmailOutboxMessage

**Table:** `email_outbox_messages`

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| RecipientEmail | string | Delivery address |
| RecipientName | string? | Display name, max 256 characters to preserve accepted member names |
| Subject | string | Email subject line |
| HtmlBody | string | Rendered HTML body |
| PlainTextBody | string? | Optional plain-text alternative; composed bodies retain action URLs, paragraph/heading/list/line-break separation, and table captions with tab-separated cells and one line per row |
| TemplateName | string | Template identifier used to render this message |
| UserId | Guid? | Bare cross-section id (optional) — no FK constraint, no nav |
| CampaignGrantId | Guid? | Bare cross-section id (CampaignGrant, Campaigns) — no FK constraint, no nav; status mirroring writes through `ICampaignWrite` |
| ReplyTo | string? | Reply-To header value |
| ExtraHeaders | string? | JSON-encoded additional headers (e.g., `List-Unsubscribe`, `Feedback-ID`) |
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

### EmailDailySendCount

**Table:** `email_daily_send_counts` — never purged; the durable denominator for spam-rate
spikes seen in Google Postmaster, since `email_outbox_messages` is pruned after
`OutboxRetentionDays` (nobodies-collective/Humans#1195).

| Property | Type | Purpose |
|----------|------|---------|
| Date | LocalDate | UTC calendar day (part of the composite PK) |
| TemplateName | string | Template identifier (part of the composite PK) |
| SentCount | int | Sends that succeeded on this UTC day for this template |
| FailedCount | int | Send attempts that failed on this UTC day for this template |

**Composite PK:** `(Date, TemplateName)`. **Index:** `Date` — the dashboard's rolling-window scan.

### CommunicationPreference

**Table:** `email_communication_preferences` (Users decomposition commit 14; replaces Users' frozen `communication_preferences`)

| Property | Type | Purpose |
|----------|------|---------|
| Id | Guid | PK |
| UserId | Guid | Bare column, no FK or nav |
| Category | MessageCategory | Stored as string (max 50) |
| OptedOut | bool? | true = no email for this category; **null = cleared row** (a reset, or a merge source) that shadows the legacy row so the fallback cannot resurrect it |
| InboxEnabled | bool | true = informational in-app notifications for this category show (actionable ones always do) |
| UpdatedAt | Instant | Last change |
| UpdateSource | string (100) | Who changed it — see [`update-source-attribution`](../../../../memory/code/update-source-attribution.md) |
| SubscribedAt | Instant? | First opt-in; never overwritten while non-null |

**Unique index:** `(UserId, Category)`.

**Legacy fallback (until the retirement PR):** a (user, category) with no row here reads Users' frozen row from `UserInfo.CommunicationPreferences` (`[Obsolete(HUM_USER_COMMPREFS)]`, read only by `CommunicationPreferenceService.LegacyRows`). Single-user reads go through `GetUserInfoAsync` (merge chains resolved forward); writes and the copy use the raw id. A write seeds its untouched fields from the legacy row; a reset with a legacy row writes a cleared row, otherwise deletes. The data migration `email.communication-preferences` (Email's `SectionDataMigrations`, run from `/Debug/DataMigrations`) copies every legacy row without a row here; cleared rows count as present.

### MessageCategory

| Value | Int | Description |
|-------|-----|-------------|
| System | 0 | Critical account/consent/security notifications. Always on. |
| ~~EventOperations~~ | 1 | **Deprecated** — split into VolunteerUpdates + TeamUpdates. Kept for DB string compatibility. |
| ~~CommunityUpdates~~ | 2 | **Deprecated** — replaced by FacilitatedMessages. Kept for DB string compatibility. |
| Marketing | 3 | Mailing list, promotions. Default: off. |
| Governance | 4 | Board voting, tier applications, role assignments, onboarding reviews. Default: on. |
| CampaignCodes | 5 | Discount codes, grants, campaign redemption codes. Always on. |
| FacilitatedMessages | 6 | User-to-user emails relayed via Humans. Default: on. |
| Ticketing | 7 | Purchase confirmations, event info. Default: on. Locked on when user has a matched ticket order: `CommunicationPreferenceService`'s member/guest toggle write refuses the change (`UpdatePreferenceAsync` returns `false`, controllers answer 400), and both preference pages render the row read-only from the same `IsTicketingLocked` rule. Unsubscribe links and MailerLite writes are not gated. |
| VolunteerUpdates | 8 | Shift changes, schedule updates, volunteer notifications. Default: on. |
| TeamUpdates | 9 | Drive permissions, team member adds/removes. Default: on. |

`Humans.Email.Contracts`. Stored as string. `IsAlwaysOn()` covers System and CampaignCodes; `MessageCategoryExtensions.ActiveCategories` is the display-order list (deprecated values omitted). Member names are persisted and carried in unsubscribe tokens, so they never change.

### EmailOutboxStatus

| Value | Description |
|-------|-------------|
| Queued | Awaiting delivery |
| Sent | Successfully delivered |
| Failed | Last attempt failed; may still retry until `RetryCount` reaches `OutboxMaxRetries` |

Stored as **string** (`HasConversion<string>()`, `HasMaxLength(20)`). The `Failed` status is a single bucket — there is no separate "permanently failed" status; whether a `Failed` row will be retried is determined by `RetryCount < OutboxMaxRetries` and `NextRetryAt`. There is also no `Sending` status — in-flight rows are tracked by `PickedUpAt` rather than a status transition.

### Settings key this section reads and writes

| Key | Purpose |
|-----|---------|
| `IsEmailSendingPaused` | When `"true"`, `ProcessEmailOutboxJob` skips processing. Read / written through `IEmailOutboxManagement.IsEmailPausedAsync` / `SetEmailPausedAsync`, which delegate to `ISettingsService` (the Settings section owns the `system_settings` table). The processor job also reads it through `IEmailOutboxManagement.IsEmailPausedAsync`. |

Per design-rules §8, each `system_settings` key is owned by its consuming section. Email owns this key (its semantics and the only reads/writes); persistence routes through `ISettingsService`. Do not touch this key from any other section.

## Routing

| Route | Auth | Controller action |
|-------|------|-------------------|
| `GET /Email/EmailOutbox` | `AdminOnly` | `EmailController.EmailOutbox` — outbox dashboard |
| `POST /Email/EmailOutbox/Pause` | `AdminOnly` | `EmailController.PauseEmailSending` — posted from the `/Settings#email` tab (peterdrier/Humans#1634) |
| `POST /Email/EmailOutbox/Resume` | `AdminOnly` | `EmailController.ResumeEmailSending` — posted from the `/Settings#email` tab |
| `POST /Email/EmailOutbox/Retry/{id}` | `AdminOnly` | `EmailController.RetryEmailOutboxMessage` |
| `POST /Email/EmailOutbox/Discard/{id}` | `AdminOnly` | `EmailController.DiscardEmailOutboxMessage` |
| `GET /Email/EmailPreview` | `AdminOnly` | `EmailController.EmailPreview` — rendered template gallery |
| `POST /Email/PreviewMarkdown` | Any authenticated human | `EmailPreviewController.PreviewMarkdown` — renders the caller's own typed subject/Markdown through the section-internal `IEmailPreviewManagement.RenderMarkdown`, for the `_EmailComposer` "Preview" button. Separate controller from `EmailController` because that one is `AdminOnly` at the class level and every compose form (not just admin ones) needs this. |
| `POST /Email/SendMarkdownToSelf` | Any authenticated human, rate-limited 5/min per human (`EmailComposerSelfSend` policy, registered in `Section.cs`) | `EmailPreviewController.SendMarkdownToSelf` — the `_EmailComposer` "Send to me" button. Queues one email to the caller's own address; takes no recipient. 401 when signed out, 422 when the caller has no notification address. |
| `GET /Email/EmailOutbox/BackfillDailyCounts` | `AdminOnly` | `EmailController.BackfillDailyCountsPreview` — backfill review step (never-overwrite preview) |
| `POST /Email/EmailOutbox/BackfillDailyCounts` | `AdminOnly` | `EmailController.BackfillDailyCounts` — backfill confirm step |
| `GET /Email/Outbox` | authenticated | `MemberOutboxController.MyOutbox` — own outbox history; takes no user id. Exempt from the Active-membership gate (`MembershipRequiredFilter`), like the rest of own-profile maintenance. |
| `GET /Email/SendMessage/{id}` | authenticated | `FacilitatedMessageController.SendMessage` — the composer for a one-off message to another member; redirects to the profile for self or an opted-out recipient. Personal only: sending as a team is Teams' `/Teams/{teamId}/SendMessage/{id}`. |
| `POST /Email/SendMessage/{id}` | authenticated, anti-forgery | `FacilitatedMessageController.SendMessage` — relays the message through `IEmailMessageFactory.FacilitatedMessage` and `IEmailService`, then audits `FacilitatedMessageSent`. Same guards as the GET, re-checked. |
| `GET /Email/Admin/{id}/Outbox` | `HumanAdminBoardOrAdmin` | `MemberOutboxController.AdminOutbox` — another user's outbox history. |
| `GET /Email/CommunicationPreferences` | authenticated | `CommunicationPreferencesController.Index` — the member's own preferences; takes no user id. Exempt from the Active-membership gate. |
| `POST /Email/CommunicationPreferences/Update` | authenticated, anti-forgery | `CommunicationPreferencesController.UpdatePreference` — one category's email/inbox flags (`[BindRequired]`; 400 on malformed binding or an always-on category); source `Profile`, actor the member. |
| `GET /Email/Guest/CommunicationPreferences` | `[AllowAnonymous]` | `GuestAccountController.CommunicationPreferences` — the profileless and unsubscribe-link variant; a valid `utoken` stands in for a session. |
| `POST /Email/Guest/CommunicationPreferences/Update` | `[AllowAnonymous]`, anti-forgery | `GuestAccountController.UpdatePreference` — source `Guest` when signed in, `MagicLink` via token. |
| `GET /Email/Unsubscribe/{token}`, `POST /Email/Unsubscribe/{token}` | anonymous | `UnsubscribeController` — confirm page (legacy campaign tokens) or redirect to the guest page (category tokens). |
| `POST /Email/Unsubscribe/OneClick` | anonymous, no anti-forgery by design | `UnsubscribeController.OneClick` — RFC 8058 one-click. |

The communication-preference routes moved from Users in decomposition commit 14 and took Email's URLs (the old `/Profile/Me/Notifications` redirect was dropped, no aliases); `/Gdpr/Guest/RequestDeletion` and `/Gdpr/Guest/CancelDeletion` went to Gdpr's `GuestDataController`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any service / job | Build a fully-rendered `EmailMessage` through the sending section's own builder (e.g. `GovernanceEmails`, `UsersEmails`) — or, for the facilitated relay alone, `IEmailMessageFactory.FacilitatedMessage` — and hand it to the single `IEmailService.SendAsync(message, ct)`. The default `IEmailService` is `OutboxEmailService`, which writes the row to `email_outbox_messages`. |
| Admin (`AdminOnly` policy) | Pause / resume outbox at `/Settings#email` (peterdrier/Humans#1634). Retry a failed message (re-queue). Discard a failed message (delete). View the outbox dashboard at `/Email/EmailOutbox`, including the configured per-run batch limit, 90-day daily send/failure volume and top templates. Preview rendered templates at `/Email/EmailPreview`. Review then confirm a one-shot backfill of daily counts from retained outbox history. |
| Any authenticated human | View own outbox (`GET /Email/Outbox`, linked from the Profile page) — emails where `UserId` matches the signed-in user. Preview a composer draft (`POST /Email/PreviewMarkdown`) and send it to themselves (`POST /Email/SendMarkdownToSelf`). |
| Anyone with an unsubscribe link | Opt out at `/Email/Unsubscribe/{token}` or `/Email/Unsubscribe/OneClick`, or manage every category at `/Email/Guest/CommunicationPreferences?utoken=…` — no login. |
| HumanAdmin, Board, Admin (`HumanAdminBoardOrAdmin` policy) | See a human's preferences read-only on `/Users/Admin/{id}` (`CommunicationPreferencesCardViewComponent`, an `AdminDetail` user part at weight 210, admin mode only). View another human's outbox (`GET /Email/Admin/{id}/Outbox`), reached from the "Email Outbox" card on `/Users/Admin/{id}` that Email contributes there as an `AdminDetail` user part (`OutboxCardViewComponent`, shown only to a `HumanAdminBoardOrAdmin` viewer in admin mode). Everyone else gets the 403 from the controller and no card. |

## Invariants

- The template gallery requests samples for `CultureCatalog.SupportedCultureCodes`; it owns sample personas, not a second supported-language roster.

- The unsubscribe footer uses `EmailMessage.Culture`, captured by opt-outable senders before their template scope ends, with English fallback for missing/unsupported culture. Regional codes use their supported base language. Shared composer previews use the viewer’s UI culture. The scalar is not stored: the outbox retains the already-rendered body; suppression, unsubscribe URLs/headers, and no-persistence system delivery keep their existing paths.

- Email links use the shared `Email:BaseUrl`, normalized by Shell to omit trailing slashes before a route is appended.

- Configured SMTP health probes propagate requested timeout/abort cancellation without logging or returning a connection failure; canceled probes start no connection.

- Outbox dashboard statistics, daily counts and the backfill review GET observe request cancellation; backfill confirmation remains an explicit audited write.

- The shared composer preview renders only its latest request per modal. Earlier successes or failures cannot overwrite the current draft’s preview, and separate modals remain independent.
- Queued subjects fit the existing 1,000-character storage limit, counting Unicode scalars. If prefixes or substitutions exceed it, the header ends with an ellipsis and the complete subject is HTML-encoded at the start of the body before HTML/plain-text composition. Subjects already within the limit and direct non-persisted messages retain their content.

- Every outgoing email queued through `OutboxEmailService` writes a row to `email_outbox_messages` before any transport attempt — the audit trail for delivery. The single exception is `EmailMessage.DoNotPersist`, which hands the message straight to `IEmailTransport` and writes no row; it is set by exactly one template, `account_deleted`, because its recipient is a human the Article 17 cascade has just erased and a row would re-create their address, name and body. Its SMTP/stub transport logs contain only the template name and failure type, never erased identity, subject or exception contents. Such a message is never retried: retrying would mean keeping the address in order to retry with it.
- `ProcessEmailOutboxJob` (Hangfire recurring, every minute — `*/1 * * * *`) selects rows with `SentAt IS NULL`, `RetryCount < OutboxMaxRetries`, `NextRetryAt <= now` (or null), and `PickedUpAt < now − 5 min` (or null). The batch is bounded by `OutboxBatchSize` and ordered **time-sensitive templates first, then FIFO by `CreatedAt` within each class** — see the priority invariant below. Selected rows are stamped `PickedUpAt = now` to block concurrent runs from picking the same rows, then sent one at a time through `IEmailTransport.SendAsync` with a 1-second throttle delay between successful sends.
- While `IsEmailSendingPaused = "true"`, the job returns immediately — no rows are picked up.
- **Time-sensitive mail jumps the queue.** `GetProcessingBatchAsync` orders rows whose `TemplateName` is one of `email_verification`, `magic_link_login`, `magic_link_signup`, `workspace_credentials` (`TimeSensitiveTemplates.Names`) ahead of every other row, then by `CreatedAt` within each of the two classes. Without this, a magic-link login lands behind whatever bulk mail is already queued and drains at the 1 send/second throttle — the user is locked out until the backlog clears (nobodies-collective/Humans#1122). No column and no schema change back this: it is ordering only, computed DB-side as a `CASE` in the `ORDER BY`. The immediate drain reads the same list, so the two cannot disagree.
- On success the row becomes `Status = Sent`, `SentAt = now`, `PickedUpAt = null`. On failure the row becomes `Status = Failed`, `RetryCount += 1`, `LastError = ex.Message` (bounded to 4000 UTF-16 code units without splitting a Unicode surrogate pair), `NextRetryAt = now + 2^(RetryCount+1) minutes`, `PickedUpAt = null`. Failed rows with `RetryCount >= OutboxMaxRetries` stop being picked up by future scans (they remain `Status = Failed` forever unless an admin retries or discards them). The job does not distinguish hard vs soft transport failures — preparation/transport exceptions increment the retry counter. Sent-state persistence and throttle failures propagate out of the batch without recording a failed delivery; a committed Sent row stays Sent even if its save acknowledgement fails.
- `Status = Sent` / `SentAt` records SMTP-server acceptance, **not** inbox delivery. Bounce processing is out of scope — a message marked `Sent` may still bounce silently at the recipient's mail server. Admins watching the outbox dashboard see SMTP outcomes, not inbox outcomes.
- Admin retry resets a row to `Status = Queued`, `RetryCount = 0`, `LastError = null`, `NextRetryAt = null`, `PickedUpAt = null`.
- Recipient addresses ending in `@localhost` or `@ticketstub.local` are short-circuit-marked `Sent` without contacting the transport (test addresses; sending real mail to them would damage sender reputation). `EmailTestAddress` is the single definition used by both the processor and daily-count backfill, so neither path can accidentally include a test delivery.
- `IEmailBodyComposer` is a section-internal abstraction so `OutboxEmailService` stays free of `IHostEnvironment`/configuration dependencies; the implementation (`BrandedEmailBodyComposer`) is section-internal too. `IImmediateOutboxProcessor` (`HangfireImmediateOutboxProcessor`) lives in `Humans.Email/Contracts/`.
- `IEmailPreviewService` is the cross-section seam for side-effect-free final-body rendering: `RenderSystemMessage(EmailMessage)` accepts only always-send system messages (opt-outable messages need recipient-specific unsubscribe policy and are rejected) for previewing a fully-built factory message. `IEmailPreviewManagement` (section-internal, extends it) adds `RenderMarkdown(subject, markdownBody, category)`, which renders arbitrary human-typed Markdown for the shared composer's "Preview" button and accepts any category, substituting a placeholder unsubscribe footer when it is opt-outable — its only consumer is `EmailPreviewController`, in this section. Both delegate to the same internal `IEmailBodyComposer`.
- **Daily send counts (#1195):** `EmailOutboxProcessor` increments `email_daily_send_counts` at `(now.InUtc().Date, TemplateName)` once per send attempt — `SentCount` on success, `FailedCount` on failure — right after the matching `MarkSentAsync`/`MarkFailedAsync` call, so it inherits the same test-address exclusion (the `continue` for `@localhost`/`@ticketstub.local` fires before either). That increment is wrapped in its own try/catch (`EmailOutboxProcessor.TryIncrementDailySendCountAsync`): a tally write failure is logged and swallowed without interrupting the batch or changing its delivery outcome. The increment runs in its own `DbContext`/`SaveChangesAsync`, separate from `MarkSentAsync`/`MarkFailedAsync` — a crash between the two calls can silently drop one message's contribution to that day's count. Accepted as a documented, narrow-window risk rather than folded into one transaction, consistent with the same swallow-on-failure trade-off. The table is never purged; `CleanupEmailOutboxJob` only ever deletes from `email_outbox_messages`.
- **Daily send counts backfill:** the admin-triggered backfill (`EmailOutboxService.BackfillDailySendCountsAsync`, behind a review step per `memory/process/no-data-backfills.md`, audited via `AuditAction.EmailDailySendCountsBackfilled`) aggregates retained `Sent` outbox rows by `SentAt`'s UTC date into `SentCount`. Per the issue #1195 spec, **failures are never backfilled** — the outbox keeps only a message's final state, not the day (or count) of each attempt, so a failed row's actual failure day can't be reconstructed; `FailedCount` stays `0` for every backfilled row, and a template that only ever failed produces no row at all. It also drops today's UTC date entirely — not just existing (Date, TemplateName) keys for it — so a day the live processor has already started counting is never merged with a historical partial count (which would otherwise permanently undercount that day). Past that, it inserts only (Date, TemplateName) combinations that have no row yet — it never overwrites processor-written or previously-backfilled data, so re-running it is a no-op past the first pass.
- **Communication preferences.** An unchanged write is a no-op (no row, no audit). Every change audits `CommunicationPreferenceChanged` on the user, with the signed-in actor where there is one. Erasure deletes every row and invalidates current and legacy unsubscribe tokens even inside their lifetime; the guest page refuses an erased account. Tokens for a merged-away account resolve to the survivor.
- `CommunicationPreferenceService` is also an `IUserDataContributor` (`CommunicationPreferences` slice: the effective rows, legacy included) and an `IUserMerge`: per category the later `UpdatedAt` wins (target keeps a known `SubscribedAt`, else takes the source's), a source-only category moves over, and the source gets cleared rows for its legacy categories.
- `EmailOutboxService` is the section's `IUserDataContributor`. Article 15 exports the human's own outbox history under the `EmailOutbox` key — the same rows `/Email/Outbox` already shows them. Article 17 deletes **every** row with a matching `UserId`, whatever its status: the retention sweep only reaches `Sent` rows past the cutoff, so failed and queued rows would otherwise outlive the erasure. The deletion-confirmation mail that follows the cascade leaves no row to delete (`DoNotPersist`) — it is sent after the collapse, so its `UserId` would resolve to null and put it out of this contributor's reach.
- On account merge, `EmailOutboxService` participates in Users' `IUserMerge` fan-out and its repository reassigns every outbox row's `UserId` from archived source to survivor in one save, regardless of delivery status. It changes no delivery/content fields and is safe to retry; the source account is tombstoned only after the fan-out completes.

## Negative Access Rules

- Regular humans **cannot** view another human's outbox or communication preferences; `/Email/CommunicationPreferences` takes no user id.
- Unsubscribe endpoints **cannot** enumerate accounts: a tampered or malformed token is `NotFound`.
- The composer's "Send to me" **cannot** reach anyone but the signed-in human: `SendMarkdownToSelf` takes no recipient parameter, resolves the address from the caller's own id, and is capped at 5 sends a minute per human.
- Services **cannot** send email by calling MailKit / `SmtpClient` / `IEmailTransport` directly — build an `EmailMessage` in the section's own `<Section>Emails` builder and route through `IEmailService.SendAsync`, which owns the outbox-versus-direct decision.
- The pause flag **cannot** be read or written by any non-Email code — other sections must not touch `system_settings` with key `IsEmailSendingPaused`. The processor job is the only Infrastructure-side reader and it goes through `IEmailOutboxManagement.IsEmailPausedAsync`.
- Outbox rows **cannot** be deleted except by `CleanupEmailOutboxJob` (retention-based), admin discard, or `EmailOutboxService.EraseForUserAsync` (GDPR Article 17). No service clears rows as a side-effect.

## Triggers

- **On enqueue (`OutboxEmailService`):** row inserted with `Status = Queued`, `CreatedAt = now`, `RetryCount = 0`, `NextRetryAt = null`, `PickedUpAt = null`. Every message gets a `Feedback-ID` header (`<templateName>:<campaignId-or-none>:<category-or-none>:humans-nobodies`) serialised into `ExtraHeaders`, so Google Postmaster's spam-rate feedback loop can be attributed per template/category/campaign. For categories that are opt-outable, the row is suppressed entirely if the recipient has opted out; otherwise unsubscribe headers (`List-Unsubscribe`, `List-Unsubscribe-Post`) join `Feedback-ID` in `ExtraHeaders` and a footer link is wrapped into the body. `DoNotPersist` messages get the same `Feedback-ID` on the direct-to-transport path (no outbox row).
- **On enqueue of a message whose `TemplateName` is one of `TimeSensitiveTemplates.Names`:** after the row is added, `IImmediateOutboxProcessor.TriggerImmediate()` is called to run the processor without waiting for the next minute tick. A trigger failure is logged without failing the successfully queued send; the recurring drain still delivers that row, and callers retain their send cooldowns. Those four names (`email_verification`, `magic_link_login`, `magic_link_signup`, `workspace_credentials`) get both the immediate run and the batch-priority ordering — one list, so the two cannot drift. See the batch-ordering bullet above.
- **On batch pick-up:** rows in the batch are stamped `PickedUpAt = now` (block window 5 minutes).
- **On successful delivery:** `Status = Sent`, `SentAt = now`, `PickedUpAt = null`. If `CampaignGrantId` is set, `ICampaignWrite.UpdateGrantEmailStatusAsync(grantId, Sent, now)` mirrors the status onto the grant — a failure there is logged and swallowed, never re-tallying the already-sent message as `Failed`. The job then sleeps 1 second before processing the next message.
- **On delivery failure (any thrown exception):** `Status = Failed`, `RetryCount += 1`, `LastError = ex.Message` (bounded to 4000 UTF-16 code units without splitting a Unicode surrogate pair), `NextRetryAt = now + 2^(RetryCount+1) minutes`, `PickedUpAt = null`. If `CampaignGrantId` is set, the grant is mirrored with `Failed` — a failure there is logged and swallowed too, so it cannot abort the rest of the batch. Once `RetryCount >= OutboxMaxRetries`, the processor query stops returning the row.
- **On admin pause:** `IsEmailSendingPaused = "true"`. The service records the acting admin after the setting save.
- **On admin resume:** `IsEmailSendingPaused = "false"`. The service records the acting admin after the setting save.
- **On admin retry of failed message:** row reset to `Status = Queued`, `RetryCount = 0`, `LastError = null`, `NextRetryAt = null`, `PickedUpAt = null`. After the save, `EmailOutboxService` records `EmailOutboxRetried` with the acting admin and message ID.
- **On admin discard:** row is deleted from `email_outbox_messages`, then audited as `EmailOutboxDiscarded`.
- **`CleanupEmailOutboxJob` (Hangfire recurring, weekly Sunday 03:00 UTC — `0 3 * * 0`):** deletes `Status = Sent` rows whose `SentAt` is older than `OutboxRetentionDays`.

- Admin retry, discard, pause and resume require a parseable actor ID before mutation. The service audits successful writes as `EmailOutboxRetried`, `EmailOutboxDiscarded`, `EmailSendingPaused` or `EmailSendingResumed`; missing messages and failed saves emit no success entry. Audit descriptions contain no recipient address or message content.

## Cross-Section Dependencies

- **Users:** `IUserEmailService.FindByAddressAsync` (exact form, verified only) — resolves `UserId` from a recipient address so `OutboxEmailService` can link outbox rows to users. `IUserService.GetUserInfoAsync` / `GetRawUserInfoAsync` / `GetAllRawUserInfosAsync` for the frozen legacy preference rows. Users' merge orchestrator fans out to Email's two `IUserMerge` implementations; Email only mutates its own rows.
- **Tickets:** `ITicketService` — the preference pages and `CommunicationPreferenceService`'s toggle write lock Ticketing on for a matched ticket holder. `Humans.Email` references `Humans.Tickets.Contracts` (a Base-only leaf) for it.
- **Consumers of `ICommunicationPreferenceService`:** Notifications (inbox switch), MailerLite (audience opt-outs and the import's `ICommunicationPreferenceWrite`), Debug (marketing set), Users (profile card's facilitated-message button), Teams (send as team: its profile buttons and composer refuse an opted-out recipient), and the views that name `MessageCategory`.
- **Campaigns:** `ICampaignWrite` receives the delivery status; Campaigns queues campaign wave messages via this section; per-grant latest-status is mirrored to `CampaignGrant.LatestEmailStatus` / `LatestEmailAt`.
- **Shifts:** `IShiftSignupService` sends approve/refuse/voluntell emails through this section.
- **Feedback:** `IFeedbackService` sends admin-reply emails through this section.
- **Onboarding:** `IOnboardingService` sends welcome emails through this section on Volunteer activation.
- **AuditLog:** `IAuditLogService` records persisted admin outbox controls with the acting admin, without recipient or message content.
- **Settings:** `ISettingsService` — `EmailOutboxService` reads / writes the `IsEmailSendingPaused` key in `system_settings` through this service (the Settings section owns the table).

## Architecture

**Owning services:** `OutboxEmailService` (`IEmailService`), `EmailOutboxService` (`IEmailOutboxManagement`), `CommunicationPreferenceService` (`ICommunicationPreferenceManagement`), `UnsubscribeService` (`IUnsubscribeService`, internal)
**Owned tables:** `email_outbox_messages`, `email_daily_send_counts`, `email_communication_preferences`
**Owned SystemSetting keys:** `IsEmailSendingPaused`
**Status:** (A) Migrated.

- The section lives at `src/Sections/Humans.Email` with its cross-section surface on the `Humans.Email.Contracts` leaf project (nobodies-collective/Humans#866, G5). Everything else — the entity, the repository, the body composer, the SMTP transport, the outbox admin surface — is `internal`.
- `IEmailOutboxRepository` (impl `src/Sections/Humans.Email/Data/EmailOutboxRepository.cs`) is the only file that touches `DbContext.EmailOutboxMessages`. `TimeSensitiveTemplates` (`Humans.Email.Contracts`) holds the template names the repository keys its batch priority ordering off, and `OutboxEmailService` keys its immediate drain off the same list. The `IsEmailSendingPaused` row in `system_settings` is no longer read or written here — `EmailOutboxService` reaches it through `ISettingsService` (Settings section owns the table). Registered Singleton via `IDbContextFactory<EmailDbContext>` so it can be injected into services and the recurring job alike.
- **Decorator decision — no caching decorator.** Outbox is a sequential queue drain, not a hot-path read shape.
- **Cross-domain navs stripped:** `EmailOutboxMessage` carries no navigation properties at all — `UserId` and `CampaignGrantId` are bare Guid columns in `EmailOutboxMessageConfiguration` with no FK constraint and no nav. A stale id is an accepted orphan on this append-only send log, pruned on age by `DeleteSentOlderThanAsync`. User display data resolves via `IUserService`; grant status mirroring goes through `ICampaignWrite`.
- **`Humans.Email.Contracts` — everything consumed from outside the section:**
  - `IEmailService` + `EmailMessage` — the one transport entry point, consumed from the other section projects that send mail (Auth, Camps, Campaigns, Consent, Events, Feedback, GoogleIntegration, Governance, Issues, Onboarding, Shifts, Surveys, Teams, Tickets, Users) plus the `Humans.Web` shell.
  - `IEmailMessageFactory` — one method, `FacilitatedMessage`: the volunteer-to-volunteer relay, the
    single template with no section vocabulary in it, consumed by Email itself, Camps and Teams (send
    as team, with the team's group address as the optional `replyTo`). Nothing else is
    added here; every other template belongs to the section that sends it.
  - `IEmailPreviewContributor` + `EmailPreviewPersona` + `EmailPreviewSample` — the fan-out a sending
    section implements to put its own templates in the `/Email/EmailPreview` gallery, so the gallery
    no longer names other sections' templates (peterdrier/Humans#1651). The gallery is built from the
    registered contributors alone, ordered ordinally by sample id; Email contributes the two
    facilitated-message samples through `FacilitatedMessagePreviews`, like any other section.
  - `TimeSensitiveTemplates` — the four template names that earn an immediate outbox drain and batch
    priority. Public so the section that owns one of those templates names the same constant the
    queue orders on.
  - `IEmailPreviewService` + `RenderedEmailPreview` — read-only final-body rendering for
    authorized cross-section preview pages. It applies the same internal branded composer as the
    outbox, creates no outbox row, and deliberately rejects opt-outable categories whose exact
    footer depends on recipient-specific send policy.
  - `ICommunicationPreferenceService` + `CommunicationPreferenceSnapshot` (reads, including the bulk `GetPreferencesForCategoryAsync`), `ICommunicationPreferenceWrite` (MailerLite's update and reset), `MessageCategory` + `MessageCategoryExtensions`. The contracts leaf references only Base.
  - `IEmailOutboxService` + `EmailOutboxMessageDto` — per-human outbox history and count, read by Email's own `MemberOutboxController` and `OutboxCardViewComponent` (the pages and card left Users in the Users decomposition); no other section calls it today.
  - `IEmailOutboxProcessor` / `IEmailOutboxRetention` — what `ProcessEmailOutboxJob` / `CleanupEmailOutboxJob` drive. Neither has a consumer outside the section any more, so both could move inward in a later pass.
  - `IImmediateOutboxProcessor` — implemented by `HangfireImmediateOutboxProcessor` under `Humans.Email/Contracts/` rather than `Jobs/`: that folder is for `IRecurringJob` implementors and `*Job`-named Hangfire jobs, and this type is neither. Both sides sit inside the section.
- **Section-internal abstractions** (`Humans.Email.Services`): `IEmailOutboxManagement` (admin surface and dashboard stats, consumed by `EmailController` and the admin tile), `EmailMessageFactory` (`IEmailMessageFactory`), `FacilitatedMessagePreviews` (`IEmailPreviewContributor`), `IEmailBodyComposer` / `BrandedEmailBodyComposer`, `IEmailTransport` / `SmtpEmailTransport` / `StubEmailTransport`, `EmailInlineStyler` (static, AngleSharp-based inline-style pass called from `BrandedEmailTemplate.Wrap`), `EmailPreviewService` (`IEmailPreviewService`), `EmailPreviewController` (`[Authorize]`, not `AdminOnly` — hosts `POST /Email/PreviewMarkdown`).
- **`EmailSettings` stays in `Humans.Base.Configuration` and is bound in Shell**, not in `Section.Register`: Auth's `MagicLinkUrlBuilder`, Email's own `UnsubscribeTokenProvider`, `SendReConsentReminderJob` and Email's own `SmtpHealthCheck` all read it. It is Base configuration the section is merely named after.
- **`EmailOutboxStatus` stays in `Humans.Base.Enums`.** Campaigns' `CampaignGrant.LatestEmailStatus` and Surveys' `SurveyInvitation` persist it on their own tables, so it is shared Base vocabulary rather than section-internal; its `Enum_EmailOutboxStatus_*` resource keys stay in `SharedResource` with it.
- **Resource set:** `Humans.Email/EmailResource.{resx,es,ca,de,fr,it}` holds the `Email_CommPrefs_*` and `Email_Unsubscribe_*` keys of the preferences and unsubscribe pages (moved from Users in decomposition commit 14), and the three `Email_FacilitatedMessage_*` keys (`Subject`, `Body`, `NoContactInfo` — the copy of the one template this section still renders), the two composer-self-test keys and the footer key, and the eight `Email_Outbox_*` keys of the member-facing outbox page (`/Email/Outbox`). The two admin dashboard views carry no localized string; the "Email Outbox" admin card is localization-exempt. Every other section's transactional copy moved into that section's own resx set with its `<Section>Emails` builder, keeping the `<Section>_Email_*` prefix (peterdrier/Humans#1651).
- **Architecture test:** `tests/Humans.Email.Tests/EmailArchitectureTests.cs` pins the `OutboxEmailService` constructor shape, that it takes no Hangfire dependency, and which side of the boundary each connector abstraction sits on. `tests/Humans.Integration.Tests/Controllers/EmailPageRenderTests.cs` is the step-12 render guard.

### Touch-and-clean guidance

- Do **not** call MailKit / `SmtpClient` / `IEmailTransport` directly from business code. Build an `EmailMessage` in your section's own `<Section>Emails` builder and route through `IEmailService.SendAsync`.
- Do **not** read or write the `IsEmailSendingPaused` `SystemSetting` key from outside this section.
- New message types add a method on the *sending section's* own `<Section>Emails` builder plus a sample on its `IEmailPreviewContributor` — never a method on `IEmailMessageFactory` or `IEmailService`. The single `IEmailService.SendAsync` (impl `OutboxEmailService`, which calls `IEmailBodyComposer` + `IEmailOutboxRepository.AddAsync`) is the one shared transport path.
- New headers (e.g., `List-Unsubscribe`, `Feedback-ID`) go in `ExtraHeaders` as JSON — do not add new columns per-header. The outbox schema is stable.
