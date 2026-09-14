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
| system_settings | R/W (key `IsEmailSendingPaused`, **via `ISettingsService`** — the Settings section owns the table) |

`IsEmailPausedAsync` / `SetEmailPausedAsync` read/write the
`IsEmailSendingPaused` key through `ISettingsService`. Cross-section
calls via `ISettingsService`, plus `IClock`. No `IMemoryCache`.

### EmailOutboxProcessor (Scoped)

Repository: `IEmailOutboxRepository`.

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W |

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

### EmailPreviewService (Scoped)

No repository — side-effect-free preview only, via the same
`IEmailBodyComposer` the outbox uses to render the send body.

| Table | R/W |
|-------|-----|
| (none) | — |

Implements `IEmailPreviewServiceRead`. `RenderSystemMessage` composes an
`EmailMessage` (system-category only) into a `RenderedEmailPreview` without
touching `EmailOutboxMessages` or any repository. No `IMemoryCache`.

---


