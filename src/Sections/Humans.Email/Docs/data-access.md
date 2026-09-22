# Email — Data Access

## Email

Folder: `src/Sections/Humans.Email/Services/`. **DbContext:**
`EmailDbContext`. `EmailOutboxRepository` injects
`IDbContextFactory<EmailDbContext>` directly. Owns `EmailOutboxMessages`.
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
| EmailDailySendCounts | R/W (dashboard reads; backfill inserts — never overwrites, #1195) |
| system_settings | R/W (key `IsEmailSendingPaused`, **via `ISettingsService`** — the Settings section owns the table) |

`IsEmailPausedAsync` / `SetEmailPausedAsync` read/write the
`IsEmailSendingPaused` key through `ISettingsService`. Cross-section
calls via `ISettingsService`, plus `IClock`. No `IMemoryCache`.

### EmailOutboxProcessor (Scoped)

Repository: `IEmailOutboxRepository`.

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |
| EmailDailySendCounts | W (increments once per send attempt, #1195) |

Drains the outbox queue (the G5 playbook, step 6b): claims a processing batch,
sends via the section-internal `IEmailTransport`, and records each outcome.
Checks the pause flag through `IEmailOutboxService.IsEmailPausedAsync` before
each run. Cross-section calls via `ICampaignService` (mirrors campaign-grant
email status after send — `[CrossSectionWrite]`-marked) and `IHumansMetrics` /
`IMeters`, plus `IClock`. No `IMemoryCache`.

### OutboxEmailService (Scoped)

Repository: `IEmailOutboxRepository`.

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |

Implements `IEmailService` — the single `SendAsync(EmailMessage)` send
path (the interface collapsed to one method). Cross-section calls via
`IUserEmailService`, `IEmailBodyComposer`, `IImmediateOutboxProcessor`,
`IHumansMetrics`, `ICommunicationPreferenceService`, plus `IClock`. No
`IMemoryCache`.

### EmailMessageFactory (Scoped, internal)

No repository. Pure builder for the one template this section still owns —
`FacilitatedMessage`, the volunteer-to-volunteer relay — reading
`EmailResource` (via `IStringLocalizer<EmailResource>`) and writing nothing.
Returns an `EmailMessage` for Users' `ProfileViewController` and Camps'
`CampContactService` to pass to `IEmailService.SendAsync`. No DB access, no
cache.

### FacilitatedMessagePreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`,
`Section.cs:52`) — builds the two facilitated-message samples via
`IEmailMessageFactory` for `/Email/EmailPreview`. No DB access, no cache.

### EmailPreviewService (Scoped)

No repository — side-effect-free preview only, via the same
`IEmailBodyComposer` the outbox uses to render the send body.

| Table | R/W |
|-------|-----|
| (none) | — |

Implements `IEmailPreviewServiceRead` (cross-section) and the section-internal
`IEmailPreviewService` it extends. `RenderSystemMessage` composes an
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
row is `OutboxEmailService`'s write), then audits `EmailComposerSelfTestSent`.
Section-internal; its only consumer is `EmailPreviewController`. No cache.

---


