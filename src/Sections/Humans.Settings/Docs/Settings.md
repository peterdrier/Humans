<!-- freshness:triggers
  src/Sections/Humans.Settings/**
  src/Sections/Humans.Camps/Services/CachingCampService.cs
  src/Sections/Humans.Events/Services/CachingEventService.cs
  src/Sections/Humans.Rideshare/Services/CachingRideshareService.cs
  src/Sections/Humans.Shifts/Services/CachingShiftViewService.cs
-->
<!-- freshness:flag-on-change
  The at-most-one-Active invariant, and "Settings mints event ids" — re-read both when the section's code changes.
-->

# Settings — Section Invariants

App-wide settings: the `system_settings` key/value store every section may read
and write through `ISettingsService`, and `settings_event`, the register of event
cycles and of which one is active.

## Concepts

- A **Setting** is one key/value row in `system_settings`. Well-known keys live
  in `SettingKeys`; values are strings, parsed by the caller.
- **EventSettings** is one event cycle's app-wide values: name, public year,
  gate-opening date, build/event/strike offsets, early-entry window and capacity,
  off-season flag. Crosses the boundary as `EventSettingsInfo`.
- **The event timezone is global.** One `system_settings` key, `Event:TimeZoneId`
  (absent means Europe/Madrid), applies to every event. `EventSettingsInfo.TimeZoneId`
  is filled from it on every read.
- **EventSettingsStatus** is `Active` (at most one row), `Inactive`, `Deleted`.
  Other sections store the id, so a cycle ends by going `Inactive`; nothing sets
  `Deleted` today. The only row removal is `IEventSettingsSeeding.DeleteEventAsync`,
  for fixture teardown.
- **Settings mints event ids.** A new cycle is created at `/Settings#event` by
  saving the form with no id. Shifts' own `event_settings` row carries only its
  section-local knobs and is created on demand the first time a rota or knob edit
  needs one.
- **`/Settings`** is the member-facing settings page: it renders whatever tabs
  sections contribute through `ISectionSettings` (in `Contracts/`, so a contributor
  opts in through the folder it already references, not through Base). Settings
  owns tab composition end to end (`SettingsTabComposition`,
  `SettingsTabsViewComponent`). A **tab label** is a `SharedResource` key
  (`Settings_TabEvent`), because the strip renders every contributor's label and
  cannot see their private resource sets.
- **The Events tab** (`/Settings#event`) is this section's own tab, pinned first
  (`Weight = int.MinValue`) because every other tab's settings hang off the event.
  An admin gets the timezone form, the event list (dates, status, Edit, Make
  active), "Create next event" (`?event=new`: the latest event's shape one year on,
  unsaved, inactive) and the event form posting to `/Settings/Admin`. Every other
  member gets the active event read-only (name, gate date, build/event/strike
  windows) and no list. Member-facing strings live in `SettingsResource`; the admin
  list and form are admin-exempt (`memory/code/localization-admin-exempt.md`).
- **`/Settings/Admin`** has no GET. Each POST is post-redirect-get on both outcomes:
  a refusal flashes the failing rule and redirects back to the tab (by id when the
  form carried one). `/Settings` is reached from the signed-in user menu
  (`ISectionChrome` → `SettingsUserMenuViewComponent`) and the admin nav.

## Data Model

### Setting

**Table:** `system_settings` (predates the `settings_*` naming convention)

| Property | Type | Notes |
|----------|------|-------|
| Key | string | PK |
| Value | string | |

### EventSettings

**Table:** `settings_event`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK — minted here for a new cycle; Shifts creates its own knobs row against this id on demand |
| EventName | string | |
| Year | int | Always `GateOpeningDate.Year`; never edited on its own |
| TimeZoneId | string | IANA; a copy of the global `Event:TimeZoneId`, written on save and never read |
| GateOpeningDate | LocalDate | Day 0 of every offset |
| BuildStartOffset | int | Negative |
| EventEndOffset / StrikeEndOffset | int | |
| EarlyEntryCapacity | JSON `Dictionary<int,int>` | Step function, day offset → capacity; dictionary equality and hashing ignore insertion order |
| BarriosEarlyEntryAllocation | JSON, nullable | |
| EarlyEntryClose | Instant, nullable | |
| EarlyEntryStartOffset | int?, nullable | Negative day offset from `GateOpeningDate`; null until configured. Validated `BuildStartOffset ≤ value < 0`. Resolved date = `GateOpeningDate.PlusDays(offset)`. Camps' `IEarlyEntryProvider` reads it via `ISettingsService`. |
| Status | EventSettingsStatus | |
| IsOffSeason | bool | Admin-set "this cycle is wrapped" switch, a checkbox on the event form. Tickets' and Shifts' member-dashboard cards hide behind it. Deriving it from `StrikeEndOffset` is deferred to the settings revamp. |
| CreatedAt / UpdatedAt | Instant | Stamped by the repository upsert |

**Indexes / constraints:** PKs only — every row rule below is service-enforced
(`memory/architecture/db-enforcement-minimal.md`).

Own `SettingsDbContext`, migrations under `Data/Migrations/`, history table
named by `SectionMigrationsHistory.TableFor`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any section (code) | Read/write `system_settings` keys and read the timezone via `ISettingsService`; read `settings_event` via `GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` / `GetAllEventSettingsAsync` |
| Any authenticated member | Views the active event's values, read-only, on the `/Settings#event` tab |
| Admin | Edits event rows, starts a new cycle, makes an event active and sets the timezone, from the `/Settings#event` tab (posts to `SettingsAdminController`) |

`SettingsAdminController` is `PolicyNames.AdminOnly`, class-level (pinned in
`tests/Humans.Settings.Tests/SettingsArchitectureTests.cs`; per-route detail in
[`authorization.md`](authorization.md)); `/Settings` itself is `[Authorize]` only.

## Invariants

- **At most one `Active` row in `settings_event`; zero is legal** (a cycle ends
  by deactivation). An admin save is refused while another row is active
  (`AnyOtherActiveEventSettingsAsync(excludingId)`); this is the only home of the
  rule — Shifts' knobs row carries no active flag of its own.
- **Switching the active event is one step.** `ActivateEventSettingsAsync` sets the
  target Active and the previous active row Inactive in one database save. The
  form's own Active box still refuses a second active row.
- **Event windows stay ordered.** `0 ≤ EventEndOffset ≤ StrikeEndOffset`:
  gate-opening day belongs to the event, and strike cannot end before the event
  does. Equal end offsets are valid (no strike days). Admin saves and seeded
  activation reject inverted windows before any write.
- **`EarlyEntryStartOffset`, when set, stays inside the build window**:
  `BuildStartOffset ≤ EarlyEntryStartOffset < 0`, enforced in
  `Service.SaveEventSettingsAsync` (`ServiceTests`); null (not yet configured)
  always passes.
- **Calendar dates must be representable.** Admin saves and seeded activation
  reject gate/offset combinations whose dates, or the following day, fall outside
  BCL date support, before any write.
- **Events never overlap.** An admin save is refused when its build-start to
  strike-end window (inclusive days) overlaps any other non-Deleted event's,
  naming that event. Several events in one year are allowed. Seeded activation
  skips the check.
- **The timezone is a known IANA id.** `SaveTimeZoneAsync` refuses anything else.
- **Admin save refusals are results.** Invalid dates, early-entry offsets,
  overlaps and competing active cycles return refusal results before any write,
  audit or listener notification. Unexpected dependency failures propagate.
  Seeded activation throws on invalid dates instead.
- **Admin writes name their actor.** Save, make active and timezone resolve the
  signed-in account before writing; a missing account is challenged and a
  cancelled lookup writes nothing.
- **Every posted event form is complete.** Every rendered field is `[BindRequired]`,
  so a missing key cannot mint a cycle or reset values through defaults. An
  explicitly blank id still starts a new cycle; blank optional early-entry values
  clear them.
- **Writes to `settings_event` stay inside the section.** The event writes live
  on the internal `ISettingsWriteService`, not on the `ISettingsService` contract.
- EF entities never leave the section; the cross-section surface is the
  `Contracts/` folder (namespace `Humans.Settings.Contracts`).

## Negative Access Rules

- `/Settings/Admin` has no GET; it 404s for everyone, admin or not.
- A non-admin **cannot** edit the Event tab: they get the read-only rendering
  (no `<form>`, no list, no inputs to POST), `?event=` is ignored for them, and a
  POST to `SettingsAdminController` still requires `PolicyNames.AdminOnly`.
- Code outside the section **cannot** write `settings_event` —
  `ISettingsService` carries no event-settings write.

## Triggers

- **Every admin write is audited** as `SettingsAuditActions.EventSettingsUpdated`,
  naming the actor: a save (entity `EventSettings`, the saved values), a make-active
  (entity `EventSettings`, the target and the rows set inactive) and a timezone
  change (entity `EventTimeZone`, old and new zone).
- **Every write that may have changed a cycle notifies every
  `IEventSettingsChangeListener`, before the audit, even when persistence throws
  after committing**: a save notifies its id, make-active the target and the
  former active row, a timezone change every event, seeding each row it changed,
  and a seeded delete unless the row was confirmed missing. Refusals notify nobody.
- `Humans.Development`'s dashboard seeder calls `IEventSettingsSeeding.CreateActiveEventAsync`
  before seeding Shifts fixtures against the same id, and `DeleteEventAsync` on
  reset. Seeding writes no audit entry — it has no real actor.

## Cross-Section Dependencies

Declared in `Humans.Settings.csproj`: `Humans.Base` only. `IAuditLogService` and
`IUserService` (the controller's base-class dependency) live in Base. Everything
below is another section reaching *in* through `Contracts/`, or implementing a seam.

The event-cycle readers are deliberately not listed: the set is most of the app,
too wide to keep in step by hand. Derive it from the call sites.

| Direction | Section | Through |
|---|---|---|
| in | every section that renders a date, a phase or an early-entry window | `ISettingsService.GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` / `GetAllEventSettingsAsync`. Not enumerated — see above |
| in | Development | `IEventSettingsSeeding`, from the dashboard seeder |
| in | Workgroups | `ISettingsService.GetValueAsync` — legacy root-folder fallback until its admin saves section-owned `workgroups_settings`; no new writes. `ISettingsService.GetTimeZoneIdAsync` — the default zone of the meeting form |
| in | Email | `ISettingsService` (`IsEmailSendingPaused`) |
| in | Monitor | `ISettingsService` (`DriveActivityMonitor:LastRunAt`) |
| in (seam) | Camps, Events, Rideshare, Shifts | implement `IEventSettingsChangeListener`: Shifts' `CachingShiftViewService` (flushes every `ShiftUserView` and forwards the id to its dashboard aggregates), Events' `CachingEventService` (its guide settings carry the timezone), Camps' `CachingCampService` (open seasons derive from events), Rideshare's `CachingRideshareService` (clears its cache). Settings names no consumer and references no consuming section |
| in (seam) | sections with settings | implement `ISectionSettings` to contribute a `/Settings` tab |

- **Audit actions:** declared on `SettingsAuditActions` (`src/Sections/Humans.Settings/SettingsAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `EventSettingsUpdated`.

## Architecture

**Owning services:** `Service`, behind `CachingSettingsService` (the Singleton
registered as `ISettingsService`, `ISettingsWriteService` and `IEventSettingsSeeding`)
**Owned tables:** `system_settings`, `settings_event`
**Status:** (A) — own project, own context, repository-only data access.
`CachingSettingsService` holds every event row and the timezone in memory, so the
event reads never reach the database; `Service` clears it on every event or timezone
write, before it tells the `IEventSettingsChangeListener`s. Plain key reads
(`GetValueAsync`) are not cached.
**Contributes:** through `Section`: `ISectionSettings` — the `/Settings#event`
tab, rendered by `EventSettingsTabViewComponent`; `ISectionChrome` — the
`/Settings` link in the signed-in user menu (`user-menu` slot); `ISectionAdminNav`
— the admin shell's "Event settings" item.

Detail on the repository surface: [`data-access.md`](data-access.md).
