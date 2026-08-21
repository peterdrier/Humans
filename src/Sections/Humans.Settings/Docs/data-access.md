# Settings — Data Access

## Settings

Folder: `src/Sections/Humans.Settings/Services/`. **DbContext:**
`SettingsDbContext`. `Repository`
(`src/Sections/Humans.Settings/Data/Repository.cs`, implements
`ISettingsRepository`) injects `IDbContextFactory<SettingsDbContext>`
directly. Owns the `system_settings` key/value table and the `app_event_settings`
table, centralizing app-wide settings persistence behind one owning
repository so consuming sections route through `ISettingsServiceRead`
instead of each touching the tables from their own repository.

### Service (Scoped)

Repository: `ISettingsRepository`. Registered against both
`ISettingsServiceRead` (the cross-section read surface) and
`ISettingsService` (reads plus writes; a section outside Settings that takes
it must declare `[CrossSectionWrite]`).

| Table | R/W |
|-------|-----|
| `system_settings` | R/W (`GetValueAsync` / `SetValueAsync`, by key) |
| `app_event_settings` | R/W (`GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` / `SaveEventSettingsAsync`) |

Thin over the repository — no business logic beyond entity↔DTO mapping, no
cross-section calls, no cache. Key/value consumers today:
`EmailOutboxService` (`IsEmailSendingPaused`) and
`DriveActivityMonitorService` (`DriveActivityMonitor:LastRunAt`);
well-known keys live in `SettingKeys` (`Humans.Settings.Contracts`).
Event-settings consumers are every section that needs the calendar anchor,
build calendar or early-entry window; they read `EventSettingsInfo`, never
the entity.

`/Settings/Admin` is the section's own admin screen for the app-wide event
values. `/Shifts/Admin/EventSettingsCarry` is the temporary operator screen
that carried them off the Shifts row (#1104); it retires with the old
columns.

---
