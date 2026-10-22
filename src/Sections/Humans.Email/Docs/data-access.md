# Email — Data Access

## Email

Folder: `src/Sections/Humans.Email/Services/`. **DbContext:**
`EmailDbContext`. `EmailOutboxRepository` injects
`IDbContextFactory<EmailDbContext>` directly, as does
`CommunicationPreferenceRepository`. Owns `EmailOutboxMessages`,
`EmailDailySendCounts` and `CommunicationPreferences`
(`email_communication_preferences`, Users decomposition commit 14).
The email-send-pause flag routes through `ISettingsService` (key
`SettingKeys.IsEmailSendingPaused`), whose table the Settings section owns
(`src/Sections/Humans.Settings/`) — reached only via the service interface,
never a cross-context repository read.

### EmailOutboxService (Scoped)

Repositories: `IEmailOutboxRepository`, plus `ISettingsService` (the
pause flag).

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W (via `IEmailOutboxRepository`) |
| EmailDailySendCounts | R/W (dashboard reads; backfill inserts — never overwrites, nobodies-collective/Humans#1195) |
| system_settings | R/W (key `IsEmailSendingPaused`, **via `ISettingsService`** — the Settings section owns the table) |

The per-user reads (`GetMessagesForUserAsync`, `GetMessageCountForUserAsync`) touch `EmailOutboxMessages` only; their callers are `MemberOutboxController` and `OutboxCardViewComponent`, both in this section.

`IsEmailPausedAsync` / `SetEmailPausedAsync` read/write the
`IsEmailSendingPaused` key through `ISettingsService`. Cross-section
calls via `ISettingsService` and `IAuditLogService` (admin retry, discard, pause and resume after save), plus `IClock`. No `IMemoryCache`.

### EmailOutboxProcessor (Scoped)

Repository: `IEmailOutboxRepository`.

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |
| EmailDailySendCounts | W (increments once per send attempt, nobodies-collective/Humans#1195) |

Drains the outbox queue (the G5 playbook, step 6b): claims a processing batch,
sends via the section-internal `IEmailTransport`, and records each outcome.
Checks the pause flag through `IEmailOutboxManagement.IsEmailPausedAsync` before
each run. Cross-section calls via `ICampaignWrite` (mirrors campaign-grant
email status after send) and `IHumansMetrics` /
`IMeters`, plus `IClock`. No `IMemoryCache`.

### OutboxEmailService (Scoped)

Repository: `IEmailOutboxRepository`.

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |

Implements `IEmailService` — the single `SendAsync(EmailMessage)` send
path (the interface collapsed to one method). The row's `UserId` is the one the
sender named; no address lookup. Cross-section calls via
`IImmediateOutboxProcessor`, `IHumansMetrics`,
plus `IClock`; `IEmailBodyComposer` and `ICommunicationPreferenceManagement`
(opt-out checks, `List-Unsubscribe` headers) are section-internal. No
`IMemoryCache`.

### CommunicationPreferenceService (Scoped)

Repository: `ICommunicationPreferenceRepository` (Singleton, `IDbContextFactory<EmailDbContext>`).

| Table | R/W |
|-------|-----|
| CommunicationPreferences | R/W |
| Users' `communication_preferences` (frozen legacy) | R only via `IUserService` (`UserInfo.CommunicationPreferences`) — never the table |

Implements `ICommunicationPreferenceService`, `ICommunicationPreferenceWrite`,
the internal `ICommunicationPreferenceManagement`, `IUserDataContributor` and
`IUserMerge`. Cross-section calls via `IUserService`, every registered `ICommunicationPreferenceLock` (Tickets locks
the Ticketing preference for ticket holders), `IAuditLogService`;
`IUnsubscribeTokenProvider` is section-internal. No cache.

### UnsubscribeService (Scoped, internal)

No repository. Validates unsubscribe tokens (category tokens through
`ICommunicationPreferenceManagement`, legacy campaign tokens through
`IDataProtectionProvider`) and opts the user out. Reads the display name
through `IUserService`. No cache.

### EmailMessageFactory (Scoped, internal)

No repository. Pure builder for the one template this section still owns —
`FacilitatedMessage`, the volunteer-to-volunteer relay — reading
`EmailResource` (via `IStringLocalizer<EmailResource>`) and writing nothing.
Returns an `EmailMessage` for Email's `FacilitatedMessageController`, Camps'
`CampContactService` and Teams' `TeamMessageController` (which passes the team's
group address as `replyTo`) to pass to `IEmailService.SendAsync`. No DB access, no
cache.

### FacilitatedMessagePreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`,
`Section.cs:52`) — builds the two facilitated-message samples via
`IEmailMessageFactory` for `/Email/EmailPreview`. No DB access, no cache.

### EmailPreviewService (Singleton)

No repository — side-effect-free preview only, via the same
`IEmailBodyComposer` the outbox uses to render the send body.

| Table | R/W |
|-------|-----|
| (none) | — |

Implements `IEmailPreviewService` (cross-section) and the section-internal
`IEmailPreviewManagement` it extends. `RenderSystemMessage` composes an
`EmailMessage` (system-category only) into a `RenderedEmailPreview` without
touching `EmailOutboxMessages` or any repository. `RenderMarkdown`
(internal-only) renders an arbitrary human-typed subject/Markdown body (via
`SanitizedMarkdownRenderer`) the same way, for the shared `_EmailComposer`
(Base) "Preview" button — served by this section's own
`EmailPreviewController` (`[Authorize]`, any authenticated human).
No `IMemoryCache`.

### ComposerSelfSendService (Scoped)

No repository. Backs the `_EmailComposer` "Send to me" button
(peterdrier/Humans#1793): resolves the caller's own notification address via
`IUserEmailService.GetNotificationTargetEmailsAsync`, renders the typed Markdown
through `SanitizedMarkdownRenderer`, and hands one `composer_self_test`
`MessageCategory.System` message to `IEmailService.SendAsync` (the outbox
row is `OutboxEmailService`'s write), then audits `EmailComposerSelfTestSent`
through `IAuditLogService`. `IUserService` supplies the caller's display name.
Section-internal; its only consumer is `EmailPreviewController`. No cache.

---


