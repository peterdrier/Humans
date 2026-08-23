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
`ISettingsService` for everyone outside the section, and as the section-internal
`ISettingsWriteService` for the section's own screens.

`SaveEventSettingsAsync` is deliberately **not** on `ISettingsService`. Nothing
outside Settings writes the event values, so the write lives on
`ISettingsWriteService`, which only `SettingsAdminController` and
`EventSettingsCarryService` inject. The key/value `SetValueAsync` does stay on
the cross-section interface, because Email's send-pause flag and Monitor's
last-run stamp have always been written from outside.

| Table | R/W |
|-------|-----|
| `system_settings` | R/W (`GetValueAsync` / `SetValueAsync`, by key) |
| `settings_event` | R/W (`GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` on the cross-section interface; `SaveEventSettingsAsync` on `ISettingsWriteService`) |

`SaveEventSettingsAsync` holds the table's two invariants — nothing else does,
because neither can be a DB constraint
(`memory/architecture/no-db-check-constraints.md`,
`memory/architecture/unique-constraints-ids-only.md`):

1. **At most one `Active` row.** `AnyOtherActiveEventSettingsAsync(excludingId)`
   is checked before a row is written `Active`; the row being saved is excluded,
   so re-saving the active row is an ordinary edit. Zero active rows is legal —
   deactivating is how a cycle ends. Mirrors Shifts'
   `ShiftManagementService.CreateAsync`/`UpdateAsync` over the same shape.
2. **A new row's id must name a Shifts event.** `Rota.EventSettingsId` and
   `EventGuideSettings.EventSettingsId` still resolve against Shifts'
   `event_settings`, so a row born here with an invented id is an event that can
   never hold a rota. Inserts check `IBurnSettingsService.GetByIdAsync` first;
   updates do not. Retires with the carry, taking the `IBurnSettingsService`
   dependency with it.

Otherwise thin over the repository — entity↔DTO mapping, no cache. Key/value
consumers today:
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

No repository of its own. Reads the Shifts rows it copies from through
`IBurnSettingsService` (`Humans.Shifts.Contracts`) — an ordinary cross-section
read — and writes `settings_event` through `ISettingsWriteService`. Driven by
`/Settings/Admin/Carry`, operator-triggered, idempotent, never on startup. Keeps
each row's `Id` so `Rota.EventSettingsId` and `EventGuideSettings.EventSettingsId`
still resolve.

**It preserves the at-most-one-`Active` invariant.** `BurnSettingsInfo` carries
no active flag, so the carry calls `IBurnSettingsService.GetActiveAsync()` once
and gives that id `Active`; every other cycle is written `Inactive`. Copying them
all as `Active` would put several live cycles in the table at once. Writes go in
two passes — every `Inactive` row first, the `Active` one last — because
`SaveEventSettingsAsync` refuses a second `Active` row, so the outgoing cycle has
to step down before the incoming one takes over.

**A rerun reconciles status, not values.** The active Shifts cycle can change
between the first carry and the reader cutover, so an existence-only check would
leave the retired cycle `Active` here forever and the cutover would select the
wrong event. A row already present is re-saved only when its status disagrees
with Shifts, and then only its `Status` changes — the app-wide values are the
operator's from the first carry on, edited on `/Settings/Admin`, and are never
re-copied from the Shifts row. `EventSettingsCarrySnapshot.PendingCount`
(`RemainingCount` + `StaleStatusCount`) is what the screen offers to run, so the
rerun affordance survives `RemainingCount == 0`.

Retires with the old columns, taking the `Humans.Shifts.Contracts` project
reference with it.

`/Settings/Admin` is the section's own admin screen for the app-wide event
values. It **edits an existing row and never creates one**: `GET` takes an
optional `id` (defaulting to the active row), a `POST` redirects back with that
id so a row the operator just deactivated stays reachable, and with no row to
show it renders `NoEvent.cshtml` pointing at the carry. The carry screen's row
table links every carried row to `/Settings/Admin?id=…` — that is the lookup for
inactive rows.

The build window is `[BuildStartOffset, 0)` and the four sub-period boundaries
partition it, so the form's rule is
`BuildStartOffset ≤ FirstCrew < SetupWeek < PreEvent < FinishingWeekend < 0`.
Build start therefore defaults to `-25`, the first-crew day; the older `-14`
default sat *after* `SetupWeekStartOffset = -16`, which left the two rules with
no satisfiable value and blocked every save.

---
