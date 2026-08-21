# Settings — Data Access

## Settings

Folder: `src/Sections/Humans.Settings/Services/`. **DbContext:**
`SettingsDbContext`. `Repository`
(`src/Sections/Humans.Settings/Data/Repository.cs`, implements
`ISettingsRepository`) injects `IDbContextFactory<SettingsDbContext>`
directly. Owns the `system_settings` key/value table and `settings_event`,
centralizing app-wide settings persistence behind one owning repository so
consuming sections route through `ISettingsService` instead of each touching
the tables from their own repository.

Every table this section owns is named `settings_*` — except `system_settings`,
which predates the convention and keeps its name.

### Service (Scoped)

Repository: `ISettingsRepository`. Registered twice against one instance: as
`ISettingsService` for everyone outside the section, and as the concrete
`Service` for the section's own screens.

`SaveEventSettingsAsync` is deliberately **not** on `ISettingsService`. Nothing
outside Settings writes the event values, so the write is reachable only by
injecting the concrete class — which only `SettingsAdminController` and
`EventSettingsCarryService` do. The key/value `SetValueAsync` does stay on the
interface, because Email's send-pause flag and Monitor's last-run stamp have
always been written from outside.

| Table | R/W |
|-------|-----|
| `system_settings` | R/W (`GetValueAsync` / `SetValueAsync`, by key) |
| `settings_event` | R/W (`GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` on the interface; `SaveEventSettingsAsync` section-internal) |

Thin over the repository — no business logic beyond entity↔DTO mapping, no
cross-section calls, no cache. Key/value consumers today:
`EmailOutboxService` (`IsEmailSendingPaused`) and
`DriveActivityMonitorService` (`DriveActivityMonitor:LastRunAt`);
well-known keys live in `SettingKeys` (`Humans.Settings.Contracts`).

**`settings_event` has no consumers yet.** Every section still reads the event
calendar off the Shifts-owned `event_settings` row through
`IBurnSettingsService`, unchanged. Pointing them at `EventSettingsInfo` here is
a separate change, and dropping the duplicated columns from `event_settings` a
third — new thing, migrate to it, retire the old thing
(nobodies-collective/Humans#1104).

`EventSettingsStatus` replaces the old `IsActive` flag: `Active` (at most one
row), `Inactive`, `Deleted`. Deleting is a status change, never a row removal —
other sections store the row's `Id`.

## EventSettingsCarryService (Scoped)

No repository of its own beyond `Service`. Reads the Shifts rows it copies from
through `IBurnSettingsService` (`Humans.Shifts.Contracts`) — an ordinary
cross-section read — and writes `settings_event` through the section's own
`Service`. Driven by `/Settings/Admin/Carry`, operator-triggered, idempotent,
never on startup. Keeps each row's `Id` so `Rota.EventSettingsId` and
`EventGuideSettings.EventSettingsId` still resolve, and marks only the cycle
that is active in Shifts as `Active`. Retires with the old columns, taking the
`Humans.Shifts.Contracts` project reference with it.

`/Settings/Admin` is the section's own admin screen for the app-wide event
values.

---
