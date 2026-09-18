<!-- freshness:triggers
  src/Sections/Humans.Settings/**
  src/Sections/Humans.Settings.Contracts/**
-->
<!-- freshness:flag-on-change
  The at-most-one-Active and id-coordination invariants, and the "nothing reads settings_event yet" staging claim — re-read all three when the section's code changes.
-->

# Settings — Section Invariants

App-wide settings: the `system_settings` key/value store every section may read
and write through `ISettingsService`, and `settings_event`, the staged new home
of the app-wide event values (nobodies-collective/Humans#1104).

## Concepts

- A **Setting** is one key/value row in `system_settings`. Well-known keys live
  in `SettingKeys` (`Humans.Settings.Contracts`); values are strings, parsed by
  the caller.
- **EventSettings** is one event cycle's app-wide values: name, public year,
  timezone, gate-opening date, build calendar, early-entry window and capacity.
  Crosses the boundary as `EventSettingsInfo`.
- **EventSettingsStatus** is `Active` (at most one row), `Inactive`, `Deleted`.
  Deleting is a status change, never a row removal — other sections store the id.
  No screen sets `Deleted` today.
- The **carry** (`/Settings/Admin/Carry`) copies the Shifts-owned event rows
  into `settings_event`, keeping ids. Transitional; retires with the nobodies-collective/Humans#1104
  cutover.
- **`/Settings`** (peterdrier/Humans#1628) is the member-facing settings page: it renders
  whatever tabs sections contribute through `ISectionSettings` (`Humans.Settings.Contracts`
  — not every section has settings, so the seam lives on the `.Contracts` leaf a
  contributor already references to opt in, not on Base). This section contributes
  the **Event** tab (`/Settings#event`), which wraps the `/Settings/Admin` form: editable
  for `PolicyNames.AdminOnly`, read-only (event name, gate date, build/event/strike
  windows as text) for every other authenticated member. `/Settings/Admin` itself now
  redirects there — a GET is a redirect, not a second live page
  (`memory/product/no-url-aliases.md`); the POST is unchanged. Reached from the signed-in
  user menu via the `user-menu` chrome slot (`SectionChrome` → `SettingsUserMenuViewComponent`),
  since nothing else links to it. Its member-facing strings live in `SettingsResource`,
  including the empty state (`Settings_NoTabs`); the `/Settings/Admin` and carry screens
  stay admin-exempt (`memory/code/localization-admin-exempt.md`). Settings owns tab
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
| Id | Guid | PK — same id as the Shifts `event_settings` row it was carried from |
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
| Admin | Edits event rows via the `/Settings#event` tab's form (posts to `SettingsAdminController`), runs the carry on `/Settings/Admin/Carry` |

Both admin controllers are `PolicyNames.AdminOnly` (pinned in
`tests/Humans.Settings.Tests/SettingsArchitectureTests.cs`; per-route detail in
[`authorization.md`](authorization.md)); `/Settings` itself is `[Authorize]` only.

## Invariants

- **At most one `Active` row in `settings_event`; zero is legal** (a cycle ends
  by deactivation). Enforced in `Service.SaveEventSettingsAsync` via
  `AnyOtherActiveEventSettingsAsync(excludingId)`; `ServiceTests` covers it.
- **A new row's id must name a Shifts event.** `Rota.EventSettingsId` and
  `EventGuideSettings.EventSettingsId` resolve against Shifts' `event_settings`,
  so inserts check `IBurnSettingsService.GetByIdAsync` first, and
  the form edits existing rows only — it never mints an id
  (`SettingsAdminControllerTests`). Retires with the carry.
- **Every successful `SaveEventSettingsAsync` call is audited.** Writes
  `AuditAction.EventSettingsUpdated` naming the actor and the saved values
  (peterdrier/Humans#1628) — both the form save and the carry's reconcile writes go
  through this one method, so both are covered.
- **Nothing reads `settings_event` yet.** Every section still reads the event
  values off the Shifts-owned row via `IBurnSettingsService`; `/Shifts/Settings`
  is the live editor until the nobodies-collective/Humans#1104 cutover. Both screens say so.
- **Writes to `settings_event` stay inside the section.**
  `SaveEventSettingsAsync` lives on the internal `ISettingsWriteService`, not on
  the `ISettingsService` contract.
- **The build window partitions.**
  `BuildStartOffset ≤ FirstCrew < SetupWeek < PreEvent < FinishingWeekend < 0`,
  validated by `EventSettingsViewModel` (`EventSettingsViewModelTests`).
- EF entities never leave the section; the cross-section surface is the
  `Humans.Settings.Contracts` leaf (`ISettingsService`, `EventSettingsInfo`,
  `SettingKeys`), referenced by consuming sections without referencing
  `Humans.Settings` itself.

## Negative Access Rules

- A non-admin **cannot** reach `/Settings/Admin/Carry` (`AdminOnly`), and a GET
  to `/Settings/Admin` redirects everyone, admin or not, to `/Settings#event`.
- A non-admin **cannot** edit the Event tab: they get the read-only rendering
  (no `<form>`, no submit button, no inputs to POST), and a POST to
  `SettingsAdminController` still requires `PolicyNames.AdminOnly` regardless of
  which page linked to it.
- Code outside the section **cannot** write `settings_event` —
  `ISettingsService` carries no event-settings write.
- The form **cannot** create an event row; only the carry inserts.

## Triggers

None — no background jobs, no notification fan-out. The carry runs only when
an admin submits `/Settings/Admin/Carry`, and its outcome renders on the same
screen. Every `SaveEventSettingsAsync` call (form save or carry reconcile)
writes one `AuditAction.EventSettingsUpdated` audit entry — see Invariants.

## Cross-Section Dependencies

| Direction | Section | Through |
|---|---|---|
| out | Shifts | `IBurnSettingsService` (carry source + insert id check) — retires with the carry |
| out | Users | `IUserServiceRead` (platform base-controller dependency only) |
| in | Email | `ISettingsService` (`IsEmailSendingPaused`) |
| in | Monitor | `ISettingsService` (`DriveActivityMonitor:LastRunAt`) |

## Architecture

**Owning services:** `Service` (registered as `ISettingsService` and
`ISettingsWriteService` against one instance; also writes `IAuditLogService`
entries on event-settings saves), `EventSettingsCarryService`
(no repository — reads Shifts contracts, writes via `ISettingsWriteService`)
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
