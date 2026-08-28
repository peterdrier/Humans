# Settings — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

Two unrelated jobs share this roof because both are "the one place the app keeps an
app-wide value".

First, a plain pigeonhole store: another part of the system leaves a small named note —
"email sending is paused", "the Drive monitor last ran at …" — and reads it back later.
The store neither understands nor acts on what is written; it only promises the note is
still there next time.

Second, the master record of an event cycle: what the event is called, which year, which
timezone, the date gates open, how the build weeks before gates are carved up, and how
many early-entry people may be on site how soon. One cycle at a time is *the* current
one; past cycles stay on file forever because other records point at them. Today this
record is a staging copy — every reader still uses the older copy kept by Shifts — and an
operator screen exists solely to walk the old rows across and keep their
active/retired flag honest until the switch-over.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "What is stored under this key?" / "Store this under this key" | Email's outbox pause, Monitor's last-run stamp | `ISettingsService.GetValueAsync` / `SetValueAsync` |
| "What is the current event cycle?" / "This cycle, by id?" | nobody yet (readers arrive at the nobodies-collective/Humans#1104 cutover); the section's own screens | `ISettingsService.GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` |
| "Save this event cycle's values" | only the section's own two screens | `ISettingsWriteService.SaveEventSettingsAsync` |
| "Which Shifts rows are here yet, and do the statuses agree?" / "Bring them across" | the carry screen | `IEventSettingsCarryService.GetSnapshotAsync` / `CarryAsync` |
| "Resolve a day offset against the calendar" | Shifts-side helpers, after cutover | `IEventSettingsInfo`'s members + `EventSettingsInfo.GetEarlyEntryCapacityForDay` |

Vocabulary: `SettingKeys` (the well-known pigeonhole names), `EventSettingsInfo` /
`IEventSettingsInfo` / `EventSettingsStatus` (the event cycle as other sections will see
it).

## 3. Structure

The shapes imply exactly today's layout:

- **A contracts leaf** holding the cross-section surface: the two-part `ISettingsService`,
  the key names, and the event-cycle read model. A project, not a folder, so
  consuming sections reference the leaf without touching the section.
- **One service** (`Service`), implementing the internal `ISettingsWriteService` (=
  contract + the event write) and registered once, resolved two ways.
- **One repository** over the section's two tables, behind `ISettingsRepository`.
- **One transitional carry service + its admin screen**, and **one edit screen** —
  both `AdminOnly`, both under `/Settings/Admin`.
- **`Section.cs` + `SectionAdminNav.cs`** and nothing else at the root.

## 4. Invariants

- **At most one `Active` event row.** Held in `SaveEventSettingsAsync` (no DB constraint
  by project rule). Zero active rows is legal — deactivating is how a cycle ends.
- **Event rows are never deleted, and their ids are never minted here** while Shifts
  still owns event identity: a new row's id must name a Shifts event row
  (insert-time check; retires with the carry).
- **The two admin screens are the only writers of `settings_event`;** the key/value
  store is written cross-section by design.
- **Deactivated rows stay reachable** — by id via the carry screen's row links; a save
  redirects back to its own id.
- **A carry rerun reconciles status only** — values an operator edited here are never
  overwritten from Shifts.
- **Both screens deny non-Admin** (`PolicyNames.AdminOnly`, class-level).

## 5. Seams

- **The nobodies-collective/Humans#1104 cutover.** `settings_event` has no readers yet; every section still reads
  the Shifts row via `IBurnSettingsService`. Pointing readers at
  `GetActiveEventSettingsAsync`/`IEventSettingsInfo` is the next PR-sized step, and
  dropping the duplicated Shifts columns the one after. The carry service, its screen,
  the id-coordination invariant and the `Humans.Shifts.Contracts` reference all retire
  then. Items touching the event read surface are shaped by this seam.
- **`SetValueAsync` on the cross-section contract.** Email's and Monitor's flags are
  planned to move into their own sections' settings, after which the key/value write
  (and possibly the store) shrinks or goes.

## 6. Deliberately not done

- **No caching decorator.** Two reads a day of two keys; the event read gains a cache
  only when real readers arrive, if ever.
- **No resx / localizer.** Both screens are admin-side (`localization-admin-exempt`).
- **No blank "create event" form.** Event identity still belongs to Shifts; the carry is
  the only birth path (`NoEvent.cshtml` says so on the screen).
- **No DB uniqueness/check constraint for the one-active rule** — project rule; the
  service guard plus tests are the enforcement.
- **No generic settings-browser UI** over `system_settings`. Two keys, both owned by
  their writing sections; a browse/edit screen would invite hand-editing runtime state.
- **Key/value rows are never deleted** — no caller needs it; absence and never-set are
  the same thing to `GetValueAsync`.

## Load-bearing weirdness

- **`IEventSettingsInfo` deliberately clones Shifts' `IBurnSettingsInfo`** (minus
  `IsShiftBrowsingOpen`). The duplication is the migration: new thing, move readers,
  retire old thing. Collapsing them now would re-couple the sections the split is
  separating.
- **The migrations-history sentinel names `system_settings`,** the pre-rename table, on
  purpose — the context rename also renamed its history table, so on an existing
  database the sentinel must name a table that predates the pending migrations
  (`Section.cs` explains).
- **`system_settings` keeps its pre-convention name** until a retirement-step rename is
  authorized; every other table here is `settings_*`.
- **The repository is a Singleton** over `IDbContextFactory` (each call opens its own
  context) — deliberate, not a Scoped-service bug.
- **`Year` is derived from the gate-opening date on save,** never edited on its own —
  the form has no Year field.
- **The carry writes Inactive rows before the Active one** so the single-active guard
  never trips mid-carry when the live cycle changed.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-28 | First doctor pass — section is young (nobodies-collective/Humans#1104) and close to target; drift is at the edges: a Contracts csproj comment naming consumers two moves stale, a dead GoogleIntegration reference, cloned clock-rule comments claiming callers that are not there yet | peterdrier/Humans#1560 |
