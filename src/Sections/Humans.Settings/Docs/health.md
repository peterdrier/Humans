# Settings — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

Three jobs share this roof, and the thread between them is "the one place the app keeps a
value that belongs to nobody in particular".

First, a plain pigeonhole store: another part of the system leaves a small named note —
"email sending is paused", "the Drive monitor last ran at …", "group folders go under this
Drive folder" — and reads it back later. The store neither understands nor acts on what is
written; it only promises the note is still there next time.

Second, the master record of an event cycle: what the event is called, which year, which
timezone, the day gates open, how the build weeks before gates are carved up, and when and
how many early-entry people may be on site. One cycle at a time is *the* current one; past
cycles stay on file forever because other records point at them. This record is now the
real one — the rest of the app reads its calendar from here — and a new cycle is born here
too. Anyone who changes it must be nameable afterwards, and every part of the app holding a
date derived from it has to be told at once.

Third, the front door for settings generally: a page any signed-in person can open, where
each part of the app that has something to configure puts a tab. This section supplies the
page, the tab strip and the ordering; it supplies exactly one tab of its own.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "What is stored under this key?" / "Store this under this key" | Email's send pause, Monitor's last-run stamp, Workgroups' Drive root | `ISettingsService.GetValueAsync` / `SetValueAsync` |
| "What is the current event cycle?" / "This cycle, by id?" | most of the app — every section that renders a date, a phase or an early-entry window, plus the section's own tab. Not enumerated on purpose — a set this wide is not maintainable by hand; derive it from the call sites | `ISettingsService.GetActiveEventSettingsAsync` / `GetEventSettingsByIdAsync` |
| "Save this cycle's values, on this person's say-so" | only the section's own POST endpoint | `ISettingsWriteService.SaveEventSettingsAsync` |
| "Mint / drop a cycle for a fixture" | the dev dashboard seeder | `IEventSettingsSeeding.CreateActiveEventAsync` / `DeleteEventAsync` |
| "The cycle moved — drop what you derived from it" | announced to every listener, named by none | `IEventSettingsChangeListener.EventSettingsChanged` |
| "Resolve a day offset against the calendar" | domain helpers holding either the entity or the DTO | `IEventSettingsInfo`'s members + `EventSettingsInfo.GetEarlyEntryCapacityForDay` |
| "What tabs does /Settings show this viewer?" / "Here is mine" | the page itself; any section that has settings | `SettingsTabComposition.ComposeAsync` / `ISectionSettings.Tabs` |

Vocabulary: `SettingKeys` (the well-known pigeonhole names), `EventSettingsInfo` /
`IEventSettingsInfo` / `EventSettingsStatus` (the event cycle as other sections see it),
`SettingsTab` (one contributed tab).

## 3. Structure

The shapes imply exactly today's layout:

- **A contracts leaf** holding everything another section names: the read/write
  `ISettingsService`, the key names, the event-cycle read model, the two seams other
  sections implement (`IEventSettingsChangeListener`, `ISectionSettings`) and the one the
  dev seeder drives (`IEventSettingsSeeding`). A project, not a folder, so a consumer
  references the leaf without touching the section.
- **One service** (`Service`), implementing the internal `ISettingsWriteService` (=
  contract + the event write) plus `IEventSettingsSeeding`, registered once and resolved
  three ways.
- **One repository** over the section's two tables, behind `ISettingsRepository`.
- **One page** (`SettingsController`, `/Settings`) that renders only a tab strip, one
  composer (`SettingsTabComposition`) and one strip renderer — the page knows nothing about
  any tab's contents.
- **One POST endpoint** (`SettingsAdminController`, `/Settings/Admin`) behind
  `AdminOnly`, with no GET: the form it serves lives in the tab, and both outcomes redirect
  back to it.
- **One contributed tab** — `SectionSettings` naming it, `EventSettingsTabViewComponent`
  choosing editable-or-read-only per viewer, `_EventSettingsForm` and the component's own
  view rendering the two faces.
- **One way in** — `SectionChrome` putting `SettingsUserMenuViewComponent` in the
  `user-menu` slot, since nothing else links to the page, plus `SectionAdminNav` pointing
  an admin at the same page's event tab.
- **`Section.cs`, `SectionAdminNav.cs`, `SectionChrome.cs`, `SectionSettings.cs`,
  `SettingsResource.cs`** at the root and nothing else.

## 4. Invariants

- **At most one `Active` event row; zero is legal.** Guarded in
  `Services/Service.cs:43` before the upsert (no DB constraint, by project rule);
  deactivating is how a cycle ends.
- **`EarlyEntryStartOffset`, when set, lies in `[BuildStartOffset, 0)`.** Enforced twice
  on purpose — `Services/Service.cs:50` is the invariant, `Models/EventSettingsViewModel.cs:88`
  is the field-level message the operator sees.
- **The build window partitions.** `BuildStartOffset ≤ FirstCrew < SetupWeek < PreEvent <
  FinishingWeekend < 0`, at `Models/EventSettingsViewModel.cs:65` and `:74`.
- **Every event-settings save is audited with its actor.**
  `Services/Service.cs:65`; the actor comes from the signed-in principal, and a request
  without one is challenged rather than saved (`Controllers/SettingsAdminController.cs:42`).
- **Every mutation of a cycle notifies the listeners** — admin save, seeded create, and a
  seeded delete that removed a row: `Services/Service.cs:73`, `:93`, `:100`.
- **Only the section writes `settings_event`.** The write sits on the internal
  `ISettingsWriteService` (`Services/ISettingsWriteService.cs:29`), off the cross-section
  contract; the key/value store is written cross-section by design.
- **`/Settings/Admin` is `AdminOnly`, class-level** (`Controllers/SettingsAdminController.cs:22`)
  and has no GET; **`/Settings` is `[Authorize]` only**
  (`Controllers/SettingsController.cs:12`) and renders for a viewer with no tabs at all.
- **A non-admin never sees the form.** `EventSettingsTabViewComponent` resolves
  `AdminOnly` itself (`ViewComponents/EventSettingsTabViewComponent.cs:24`), and the
  `?event=` row selector is ignored for anyone who fails it (`:29`).
- **One tab per key, first contributor wins** — `ViewComponents/SettingsTabComposition.cs:25`
  drops a duplicate rather than merging it, and a tab whose `Policy` the viewer fails is
  dropped before its component is ever invoked (`:28`).
- **Deactivated rows stay reachable** by id: a save redirects to its own id
  (`Controllers/SettingsAdminController.cs:59`) and the tab honours that id for an admin.

## 5. Seams

- **`SetValueAsync` on the cross-section contract.** Email's and Monitor's flags are
  planned to move into their own sections' settings, after which the key/value write (and
  possibly the store) shrinks or goes.
- **Dropping the dead app-wide columns from Shifts' `event_settings`.** Every reader resolves
  the cycle here now; the duplicated columns on the Shifts row survive under the
  no-drops-until-prod-verified rule and are Peter's call, not this section's.
- **Weight and the duplicate-key rule on `ISectionSettings`.** Nine sections contribute a
  tab, so the policy filter earns its place, but every contribution today takes the default
  weight and no two claim a key — the ordering and collision rules are reserved for a
  contributor that needs them, not dead code.

## 6. Deliberately not done

- **No caching decorator.** Two key reads a day; the event read is one small row, and its
  consumers already cache what they derive from it — that is what the change-listener seam
  is for.
- **No blank-form tab for a non-admin.** A viewer who cannot edit and has no active cycle
  gets the empty-state line, not an inert form.
- **No DB uniqueness/check constraint for the one-active rule** — project rule; the service
  guard plus tests are the enforcement.
- **No generic settings-browser UI** over `system_settings`. Three keys, each owned by its
  writing section; a browse/edit screen would invite hand-editing runtime state.
- **Key/value rows are never deleted** — no caller needs it; absence and never-set are the
  same thing to `GetValueAsync`.
- **No resx for the edit form.** Only an admin is ever rendered it, so it is exempt
  (`memory/code/localization-admin-exempt.md`); the read-only rendering an ordinary member
  sees is fully localized.
- **No `Deleted` path on the form.** The status is kept for removing a test event once one is
  made; no screen sets it today, and that is the intended state (Peter, 2026-09-21).
- **No re-render of a rejected blank new-cycle form.** Minting a cycle means posting the form
  with no id, so a validation failure redirects to `/Settings#event` with nothing to re-render
  and the operator retypes. Accepted rather than fixed: a first-time mint happens about once a
  year and only fails if the operator fights the field rules (Peter, 2026-09-21). Editing an
  existing row is unaffected — it redirects to its own id.

## Load-bearing weirdness

- **The migrations-history sentinel names `system_settings`,** the pre-rename table, on
  purpose — the context rename also renamed its history table, so on an existing database
  the sentinel must name a table that predates the pending migrations (`Section.cs`
  explains).
- **`system_settings` keeps its pre-convention name** until a retirement-step rename is
  authorized; every other table here is `settings_*`.
- **The repository is a Singleton** over `IDbContextFactory` (each call opens its own
  context) — deliberate, not a Scoped-service bug.
- **`Year` is derived from the gate-opening date on save,** never edited on its own — the
  form has no Year field.
- **The tab label lives in `SharedResource`, not this section's resx.** The strip renders
  every contributor's label and cannot see any contributor's private resource set —
  carve by renderer, not by owner.
- **The tab strip ships an inline script.** Bootstrap ignores the URL fragment, so without
  it every POST redirect lands on the first tab.
- **`IEventSettingsInfo`'s two clock rules are `sealed` default members** so no implementer
  can override the rule, which also lets the DTO forward to them by name.
- **The seeding seam really deletes rows.** Every other path treats a cycle as permanent;
  `DeleteEventAsync` exists for fixture teardown only and is not a production operation.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-28 | First doctor pass — section is young (nobodies-collective/Humans#1104) and close to target; drift is at the edges: a Contracts csproj comment naming consumers two moves stale, a dead GoogleIntegration reference, cloned clock-rule comments claiming callers that are not there yet | peterdrier/Humans#1560 |
| 2 | 2026-09-21 | Second pass after the peterdrier/Humans#1628 + nobodies-collective/Humans#1631 rebuild — structure matches target; the drift is the comments the rebuild left behind, all claiming the pre-cutover world | peterdrier/Humans#1778 |
