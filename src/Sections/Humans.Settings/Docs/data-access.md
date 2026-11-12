# Settings — Data Access

## Settings

Folder: `src/Sections/Humans.Settings/Services/`. **DbContext:**
`SettingsDbContext`. `Repository`
(`src/Sections/Humans.Settings/Data/Repository.cs`, implements
`ISettingsRepository`) injects `IDbContextFactory<SettingsDbContext>`
directly. Owns the `system_settings` key/value table and `settings_event`, so
consuming sections route through `ISettingsService` instead of touching the
tables from their own repository.

Every table this section owns is named `settings_*` — except `system_settings`,
which predates the convention and keeps its name.

### Service (Scoped)

Repository: `ISettingsRepository` (Singleton over
`IDbContextFactory<SettingsDbContext>`). One instance, three ways in: as
`ISettingsService` for everyone outside the section, as the section-internal
`ISettingsWriteService` for the section's own controller and view component, and
as `IEventSettingsSeeding` for `Humans.Development`'s dashboard seeder.

The event writes (`SaveEventSettingsAsync`, `ActivateEventSettingsAsync`,
`SaveTimeZoneAsync`) are deliberately **not** on `ISettingsService`: nothing
outside Settings writes them. Each writes one `IAuditLogService` entry
(`SettingsAuditActions.EventSettingsUpdated`) naming the actor. Why the key/value
`SetValueAsync` does stay on the cross-section interface: see `ISettingsService`.

| Table | R/W |
|-------|-----|
| `system_settings` | R/W (`GetValueAsync` / `SetValueAsync`, by key; `GetTimeZoneIdAsync` on the cross-section interface and `SaveTimeZoneAsync` on `ISettingsWriteService` for `Event:TimeZoneId`) |
| `settings_event` | R/W (`GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` / `GetAllEventSettingsAsync` on the cross-section interface; `SaveEventSettingsAsync`, `ActivateEventSettingsAsync` on `ISettingsWriteService`; `CreateActiveEventAsync` / `DeleteEventAsync` on `IEventSettingsSeeding`) |

The table's row rules (one active row, ordered windows, early-entry range,
representable dates, no overlaps) are service-enforced, never DB constraints
(`memory/architecture/db-enforcement-minimal.md`); [`Settings.md`](Settings.md)
states them.

Key/value consumers today: `EmailOutboxService` (`IsEmailSendingPaused`),
`DriveActivityMonitorService` (`DriveActivityMonitor:LastRunAt`) and
`WorkgroupDriveFolders` (`Workgroups:RootDriveFolderId`, legacy read fallback
only); well-known keys live in `SettingKeys`.

Shifts resolves the calendar through `EventCalendarResolver`, which wraps this
section's `ISettingsService`. The build window is `[BuildStartOffset, 0)`; its
sub-periods are Shifts' own (`BuildSubPeriod`).
