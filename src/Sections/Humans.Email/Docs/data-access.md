# Email — Data Access

## Email

Folder: `src/Sections/Humans.Email/Services/`. **DbContext:**
`EmailDbContext`. `EmailOutboxRepository` injects
`IDbContextFactory<EmailDbContext>` directly. Owns `EmailOutboxMessages`.
The email-send-pause flag routes through `ISystemSettingsService` (key
`SystemSettingKeys.IsEmailSendingPaused`), which lives in the separately-owned
`SystemSettingsDbContext` (own project,
`src/Sections/Humans.SystemSettings/`) — reached only via the service
interface, never a cross-context repository read.

### EmailOutboxService (Scoped)

Repositories: `IEmailOutboxRepository`, plus `ISystemSettingsService` (the
pause flag).

| Table | R/W |
|-------|-----|
| EmailOutboxMessages | R/W (via `IEmailOutboxRepository`) |
| SystemSettings | R/W (key `IsEmailSendingPaused`, **via `ISystemSettingsService`** — SystemSettings section owns the table) |

`IsEmailPausedAsync` / `SetEmailPausedAsync` read/write the
`IsEmailSendingPaused` key through `ISystemSettingsService`. Cross-section
calls via `ISystemSettingsService`, plus `IClock`. No `IMemoryCache`.

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

---


