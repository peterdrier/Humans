# SystemSettings — Data Access

## SystemSettings

Folder: `src/Sections/Humans.SystemSettings/Services/`. **DbContext:**
`SystemSettingsDbContext`. `Repository`
(`src/Sections/Humans.SystemSettings/Data/Repository.cs`, implements
`ISystemSettingsRepository`) injects
`IDbContextFactory<SystemSettingsDbContext>` directly. Owns the
`SystemSetting` key/value table, centralizing `SystemSettings` persistence
behind one owning repository so consuming sections route through
`ISystemSettingsService` instead of each touching the table from their own
repository.

### SystemSettingsService (Scoped)

Repository: `ISystemSettingsRepository`.

| Table | R/W |
|-------|-----|
| SystemSetting | R/W (`GetValueAsync` / `SetValueAsync`, by key) |

Thin pass-through over the repository — no business logic, no cross-section
calls, no cache. Consumers today: `EmailOutboxService`
(`IsEmailSendingPaused`) and `DriveActivityMonitorService`
(`DriveActivityMonitor:LastRunAt`). Well-known keys live in
`SystemSettingKeys` (`Humans.Domain.Constants`).

---


