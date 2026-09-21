<!-- freshness:triggers
  src/Sections/Humans.Settings/**
  src/Sections/Humans.Settings.Contracts/**
-->
<!-- freshness:flag-on-change
  The at-most-one-Active invariant, and "Settings mints event ids" — re-read both when the section's code changes.
-->

# Settings — Section Invariants

App-wide settings: the `system_settings` key/value store every section may read
and write through `ISettingsService`, and `settings_event`, the home of the
app-wide event calendar and "which cycle is active" (nobodies-collective/Humans#1104,
nobodies-collective/Humans#1630, nobodies-collective/Humans#1631).

## Concepts

- A **Setting** is one key/value row in `system_settings`. Well-known keys live
  in `SettingKeys` (`Humans.Settings.Contracts`); values are strings, parsed by
  the caller.
- **EventSettings** is one event cycle's app-wide values: name, public year,
  timezone, gate-opening date, build calendar, early-entry window and capacity.
  Crosses the boundary as `EventSettingsInfo`.
- **EventSettingsStatus** is `Active` (at most one row), `Inactive`, `Deleted`.
  No production path removes a row — other sections store the id — so a cycle
  ends by going `Inactive`, and nothing sets `Deleted` today. The one real row
  removal is `IEventSettingsSeeding.DeleteEventAsync`, for fixture teardown.
- **Settings mints event ids.** A brand-new cycle is created here, at
  `/Settings#event`, by leaving the form blank — no id, no existing row. Shifts
  no longer mints ids or a calendar of its own (nobodies-collective/Humans#1631);
  its own `event_settings` row (if any) only ever carries its section-local
  knobs, created on demand the first time a rota or knob edit needs one.
- **`/Settings`** (peterdrier/Humans#1628) is the member-facing settings page: it renders
  whatever tabs sections contribute through `ISectionSettings` (`Humans.Settings.Contracts`
  — not every section has settings, so the seam lives on the `.Contracts` leaf a
  contributor already references to opt in, not on Base). This section contributes
  the **Event** tab (`/Settings#event`), which wraps the `/Settings/Admin` form: editable
  for `PolicyNames.AdminOnly`, read-only (event name, gate date, build/event/strike
  windows as text) for every other authenticated member. `/Settings/Admin` has no GET —
  it removed the redirect-only page (`memory/product/no-url-aliases.md`); only the POST
  remains, saving back to `/Settings#event`. With no GET to re-render, the POST is
  post-redirect-get on both outcomes: validation, parse and activation-conflict failures
  flash the failing rule and redirect back to the tab (by id when the form carried one),
  exactly as every other settings tab's POST does. Reached from the signed-in
  user menu via the `user-menu` chrome slot (`SectionChrome` → `SettingsUserMenuViewComponent`),
  since nothing else links to it. Its member-facing strings live in `SettingsResource`,
  including the empty state (`Settings_NoTabs`); `/Settings/Admin` stays admin-exempt
  (`memory/code/localization-admin-exempt.md`). Settings owns tab
  composition end to end (`SettingsTabComposition`, `SettingsTabsViewComponent`) — a
  domain-owning section composes contributions into its own domain, unlike navigational
  composition, which stays in the Shell — but a **tab label** is different: it may come
  from any contributing section, so it stays in `SharedResource` (`Settings_TabEvent`),
  carved by renderer: the composer cannot see any contributor's private resource set,
  including a `.Contracts` leaf's, which carries no localized strings today anyway.

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
| Id | Guid | PK — minted here for a new cycle (nobodies-collective/Humans#1631); Shifts creates its own knobs row against this id on demand |
| EventName | string | |
| Year | int | Always `GateOpeningDate.Year`; never edited on its own |
| TimeZoneId | string | IANA |
| GateOpeningDate | LocalDate | Day 0 of every offset |
| BuildStartOffset | int | Negative; `≤ FirstCrewStartOffset` |
| EventEndOffset / StrikeEndOffset | int | |
| FirstCrew/SetupWeek/PreEventWeek/FinishingWeekendStartOffset | int | Strictly ascending, all negative |
| EarlyEntryCapacity | JSON `Dictionary<int,int>` | Step function, day offset → capacity |
| BarriosEarlyEntryAllocation | JSON, nullable | |
| EarlyEntryClose | Instant, nullable | |
| EarlyEntryStartOffset | int?, nullable | Negative day offset from `GateOpeningDate`; null until configured. Validated `BuildStartOffset ≤ value < 0`. Resolved date = `GateOpeningDate.PlusDays(offset)`. Moved from Camps' `CampSettings.EeStartDate` (nobodies-collective#1633); Camps' `IEarlyEntryProvider` reads it via `ISettingsService`. No data carried across sections — an admin re-enters the value here after the cutover. |
| Status | EventSettingsStatus | |
| CreatedAt / UpdatedAt | Instant | Stamped by the repository upsert |

**Indexes / constraints:** PKs only — both invariants below are service-enforced
(`memory/architecture/no-db-check-constraints.md`).

Own `SettingsDbContext`, migrations under `Data/Migrations/`, history table
`__EFMigrationsHistory_Settings`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any section (code) | Read/write `system_settings` keys via `ISettingsService`; read `settings_event` via `GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` |
| Any authenticated member | Views the active event's values, read-only, on the `/Settings#event` tab |
| Admin | Edits event rows, or starts a new cycle by leaving the form blank, via the `/Settings#event` tab's form (posts to `SettingsAdminController`) |

`SettingsAdminController` is `PolicyNames.AdminOnly`, class-level (pinned in
`tests/Humans.Settings.Tests/SettingsArchitectureTests.cs`; per-route detail in
[`authorization.md`](authorization.md)); `/Settings` itself is `[Authorize]` only.

## Invariants

- **At most one `Active` row in `settings_event`; zero is legal** (a cycle ends
  by deactivation). Enforced in `Service.SaveEventSettingsAsync` via
  `AnyOtherActiveEventSettingsAsync(excludingId)`; `ServiceTests` covers it.
  This is the *only* home of the invariant — Shifts' own knobs row carries no
  `IsActive` check of its own (nobodies-collective/Humans#1631).
- **Settings mints ids for new cycles, with no existence check elsewhere.**
  `Rota.EventSettingsId` and `EventGuideSettings.EventSettingsId` resolve
  against `settings_event`, not the other way around — a Shifts knobs row for
  a new id is created on demand, the first time a rota or knob edit needs it,
  never checked for on the way in here.
- **Every successful `SaveEventSettingsAsync` call is audited.** Writes
  `AuditAction.EventSettingsUpdated` naming the actor and the saved values
  (peterdrier/Humans#1628).
- **Every section reads the calendar from `settings_event`.** Repointed off the
  Shifts-owned row in nobodies-collective/Humans#1629/#1630; `/Settings#event` is the
  only editor, `/Shifts/Settings` is knobs-only.
- **Writes to `settings_event` stay inside the section.**
  `SaveEventSettingsAsync` lives on the internal `ISettingsWriteService`, not on
  the `ISettingsService` contract.
- **The build window partitions.**
  `BuildStartOffset ≤ FirstCrew < SetupWeek < PreEvent < FinishingWeekend < 0`,
  validated by `EventSettingsViewModel` (`EventSettingsViewModelTests`).
- **`EarlyEntryStartOffset`, when set, stays inside the build window.**
  `BuildStartOffset ≤ EarlyEntryStartOffset < 0`, enforced in both
  `Service.SaveEventSettingsAsync` and `EventSettingsViewModel` (`ServiceTests`,
  `EventSettingsViewModelTests`); null (not yet configured) always passes.
- EF entities never leave the section; the cross-section surface is the
  `Humans.Settings.Contracts` leaf (`ISettingsService`, `EventSettingsInfo`,
  `SettingKeys`), referenced by consuming sections without referencing
  `Humans.Settings` itself.

## Negative Access Rules

- `/Settings/Admin` has no GET; it 404s for everyone, admin or not.
- A non-admin **cannot** edit the Event tab: they get the read-only rendering
  (no `<form>`, no submit button, no inputs to POST), and a POST to
  `SettingsAdminController` still requires `PolicyNames.AdminOnly` regardless of
  which page linked to it.
- Code outside the section **cannot** write `settings_event` —
  `ISettingsService` carries no event-settings write.

## Triggers

None — no background jobs, no notification fan-out. Every `SaveEventSettingsAsync`
call writes one `AuditAction.EventSettingsUpdated` audit entry — see Invariants.
`Humans.Development`'s dashboard seeder calls `IEventSettingsSeeding.CreateActiveEventAsync`
before seeding Shifts fixtures against the same id, and `DeleteEventAsync` on reset to drop
that row again (no audit entry either way — seeding has no real actor).

## Cross-Section Dependencies

Declared in `Humans.Settings.csproj`: `Humans.Base` and `Humans.AuditLog.Contracts`
(`Humans.Settings.Contracts` is this section's own leaf). Everything else below is a
section reaching *in* through this section's leaf, or a seam it implements.

The **out** rows are complete — they are this section's own dependencies. Of the **in**
rows, the key/value ones are complete too, because `SettingKeys` bounds them. The
event-cycle readers are deliberately not listed: the set is most of the app, too wide to
keep in step by hand. Derive it from the call sites, not from here.

| Direction | Section | Through |
|---|---|---|
| out | AuditLog | `IAuditLogService` — one entry per `SaveEventSettingsAsync` |
| out | Users | `IUserServiceRead` (platform base-controller dependency, reached through Base) |
| in | every section that renders a date, a phase or an early-entry window | `ISettingsService.GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync`. Not enumerated — see above |
| in | Development | `IEventSettingsSeeding`, from the dashboard seeder |
| in | Workgroups | `ISettingsService` (`Workgroups:RootDriveFolderId`) |
| out | every `IEventSettingsChangeListener` | fanned out after every successful event-settings mutation, the admin save and both `IEventSettingsSeeding` paths (seeded upsert, delete of a row that existed) alike — the gate date, the offsets and the active-event flip move derived dates for every member at once. The notification carries the event settings id. Subscribers today: EarlyEntry's cache (`InvalidateAll`, id ignored), Shifts' `CachingShiftViewService` (flushes every `ShiftUserView`, and evicts that event's coordinator-dashboard aggregates through `IShiftManagementService.InvalidateDashboardCaches`), and Events' `CachingEventService` (its `EventGuideSettingsView` carries the Settings-owned `TimeZoneId`). Settings names no consumer and references no consuming section |
| in | Email | `ISettingsService` (`IsEmailSendingPaused`) |
| in | Monitor | `ISettingsService` (`DriveActivityMonitor:LastRunAt`) |

## Architecture

**Owning services:** `Service` (registered as `ISettingsService`,
`ISettingsWriteService` and `IEventSettingsSeeding` against one instance; also
writes `IAuditLogService` entries on event-settings saves)
**Owned tables:** `system_settings`, `settings_event`
**Status:** (A) — own project, own context, repository-only data access; no
caching decorator (low-traffic key reads, admin-only screens).
**Contributes:** `SectionSettings : ISectionSettings` — the `/Settings#event`
tab, rendered by `EventSettingsTabViewComponent`; `SectionChrome : ISectionChrome` —
the `/Settings` link in the signed-in user menu (`user-menu` slot). Also owns
composing every section's `ISectionSettings` contributions into the `/Settings`
tab strip (`SettingsTabComposition`, `SettingsTabsViewComponent`) — Settings
owns all of settings management, contributed tabs included, not just its own.

Detail on the repository surface and both invariants:
[`data-access.md`](data-access.md).
