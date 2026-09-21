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

Repository: `ISettingsRepository` (Singleton over
`IDbContextFactory<SettingsDbContext>`). One instance, three ways in: as
`ISettingsService` for everyone outside the section, as the section-internal
`ISettingsWriteService` for the section's own screens, and as
`IEventSettingsSeeding` for `Humans.Development`'s dashboard seeder.

`SaveEventSettingsAsync` is deliberately **not** on `ISettingsService`. Nothing
outside Settings writes the event values, so the write lives on
`ISettingsWriteService`, which only `SettingsAdminController` injects. Why the
key/value `SetValueAsync` does stay on the cross-section interface: see
`ISettingsService`.

Every `SaveEventSettingsAsync` call also writes one `IAuditLogService` entry
(`AuditAction.EventSettingsUpdated`, peterdrier/Humans#1628) naming the actor and the
saved values — the write is otherwise unchanged, so this is Audit's crosscut
table, not a new table this section owns.

| Table | R/W |
|-------|-----|
| `system_settings` | R/W (`GetValueAsync` / `SetValueAsync`, by key) |
| `settings_event` | R/W (`GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` on the cross-section interface; `SaveEventSettingsAsync` on `ISettingsWriteService`) |

`SaveEventSettingsAsync` holds the table's two service-enforced invariants —
neither can be a DB constraint (`memory/architecture/no-db-check-constraints.md`):

- **At most one `Active` row.** `AnyOtherActiveEventSettingsAsync(excludingId)`
  is checked before a row is written `Active`; the row being saved is excluded,
  so re-saving the active row is an ordinary edit. Zero active rows is legal —
  deactivating is how a cycle ends.
- **`EarlyEntryStartOffset`, when set, stays inside the build window.**
  `BuildStartOffset ≤ value < 0`; null (not yet configured) passes. The form
  checks it too, but the service is the backstop for the seeding seam and any
  future caller.

**Settings mints ids for new cycles** (nobodies-collective/Humans#1631) — a blank id on
save is a brand-new row, with no existence check against anything else.
`Rota.EventSettingsId` and `EventGuideSettings.EventSettingsId` resolve against
`settings_event`, not the other way; a Shifts knobs row for a new id is created
on demand, the first time a rota or knob edit needs one.

Otherwise thin over the repository — entity↔DTO mapping, no cache. Key/value
consumers today: `EmailOutboxService` (`IsEmailSendingPaused`),
`DriveActivityMonitorService` (`DriveActivityMonitor:LastRunAt`) and
`WorkgroupService` (`Workgroups:RootDriveFolderId`, read *and* written);
well-known keys live in `SettingKeys` (`Humans.Settings.Contracts`).

**Every section reads the calendar from `settings_event`.** Repointed off the
old Shifts-owned row in nobodies-collective/Humans#1629/#1630; Shifts now resolves it
through `EventCalendarResolver` wrapping this section's `ISettingsService`.

`EventSettingsStatus` replaces the old `IsActive` flag: `Active` (at most one
row), `Inactive`, `Deleted`. No production path removes a row — other sections
store the `Id` — so a cycle ends by going `Inactive`, and nothing sets `Deleted`
today. The one real row removal is `IEventSettingsSeeding.DeleteEventAsync`,
for fixture teardown.

`/Settings/Admin` POST is the section's own write for the app-wide event
values (no GET — the form lives on the `/Settings#event` tab); it redirects
back to that tab with the saved id so a row the operator just deactivated
stays reachable. Leaving the id blank on save mints a brand-new cycle
(nobodies-collective/Humans#1631) — the only birth path for a `settings_event`
row.

The build window is `[BuildStartOffset, 0)` and the four sub-period boundaries
partition it, so the form's rule is
`BuildStartOffset ≤ FirstCrew < SetupWeek < PreEvent < FinishingWeekend < 0`.
Build start therefore defaults to `-25`, the first-crew day.
