# Settings — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

Three jobs share this roof, and the thread between them is "the one place the app keeps a
value that belongs to nobody in particular".

First, a plain pigeonhole store: another part of the system leaves a small named note —
"email sending is paused", "the Drive monitor last ran at …" — and reads it back later. The
store neither understands nor acts on what is written; it only promises the note is still
there next time.

Second, the register of event cycles: what each event is called, the day its gates open,
how many days of build come before and of event and strike after, and when and how many
early-entry people may be on site. One clock zone applies to every event. At most one
cycle is the current one; past and future cycles stay on file because other records point
at them, and their build-to-strike spans never overlap. A new cycle is born here, a
different cycle is made current here, and the clock zone is changed here. Anyone who
changes any of it must be nameable afterwards, and every part of the app holding a date
derived from it has to be told at once.

Third, the front door for settings generally: a page any signed-in person can open, where
each part of the app that has something to configure puts a tab. This section supplies the
page, the tab strip and the ordering; it supplies exactly one tab of its own.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "What is stored under this key?" / "Store this under this key" | Email's send pause, Monitor's last-run stamp, Workgroups' legacy Drive root | `ISettingsService.GetValueAsync` / `SetValueAsync` |
| "Which zone are events read in?" | the section's own read path, Workgroups' meeting form | `ISettingsService.GetTimeZoneIdAsync` |
| "What is the current cycle?" / "This cycle, by id?" / "Every cycle" | most of the app; derive the set from the call sites | `ISettingsService.GetActiveEventSettings` / `GetEventSettingsByIdAsync` / `GetAllEventSettingsAsync` |
| "Save this cycle / make this one current / change the zone, on this person's say-so" | only the section's own POST endpoints | `ISettingsWriteService.SaveEventSettingsAsync` / `ActivateEventSettingsAsync` / `SaveTimeZoneAsync` |
| "Mint / drop a cycle for a fixture" | the dev dashboard seeder | `IEventSettingsSeeding.CreateActiveEventAsync` / `DeleteEventAsync` |
| "The cycle moved — drop what you derived from it" | announced to every listener, named by none | `IEventSettingsChangeListener.EventSettingsChanged` |
| "Resolve a day offset against the calendar" | domain helpers holding either the entity or the DTO | `IEventSettingsInfo`'s members + `EventSettingsInfo.GetEarlyEntryCapacityForDay` |
| "What tabs does /Settings show this viewer?" / "Here is mine" | the page itself; any section that has settings | `SettingsTabComposition.ComposeAsync` / `ISectionSettings.Tabs` |

Vocabulary: `SettingKeys` (the well-known pigeonhole names), `EventSettingsInfo` /
`IEventSettingsInfo` / `EventSettingsStatus` (the event cycle as other sections see it),
`SettingsTab` (one contributed tab).

## 3. Structure

The shapes imply exactly today's layout:

- **A `Contracts/` folder** holding everything another section names: the read/write
  `ISettingsService`, the key names, the event-cycle read model, the two seams other
  sections implement (`IEventSettingsChangeListener`, `ISectionSettings`) and the one the
  dev seeder drives (`IEventSettingsSeeding`).
- **One service** (`Service`), implementing the internal `ISettingsWriteService` (=
  contract + the event writes) plus `IEventSettingsSeeding`, reached through
  `CachingSettingsService`, which keeps the event rows and timezone in memory because
  every section asks for the active event on nearly every request.
- **One repository** over the section's two tables, behind `ISettingsRepository`.
- **One page** (`SettingsController`, `/Settings`) that renders only a tab strip, one
  composer (`SettingsTabComposition`) and one strip renderer — the page knows nothing about
  any tab's contents.
- **One POST-only controller** (`SettingsAdminController`, `/Settings/Admin`) behind
  `AdminOnly`, with no GET: save, make active and set timezone, each redirecting back to the
  tab.
- **One contributed tab** — `ISectionSettings` naming it, `EventSettingsTabViewComponent`
  choosing editable-or-read-only per viewer, `_EventList` and `_EventSettingsForm` for the
  admin face, the component's own view for the read-only face.
- **Ways in** — `ISectionChrome` putting `SettingsUserMenuViewComponent` in the `user-menu`
  slot, and `ISectionAdminNav` pointing an admin at the same page.
- **`Section.cs`, `SettingsAuditActions.cs` and `SettingsResource.cs`** at the root and
  nothing else.

## 4. Invariants

- **At most one `Active` event row; zero is legal.** Admin save refuses a second active row
  (`Services/Service.cs:72`); make-active flips the previous active row in the same save
  (`Data/Repository.cs:117`); seeding deactivates the current row first
  (`Services/Service.cs:187`).
- **Event windows are ordered:** `0 ≤ EventEndOffset ≤ StrikeEndOffset`
  (`Services/Service.cs:213`).
- **`EarlyEntryStartOffset`, when set, lies in `[BuildStartOffset, 0)`**
  (`Services/Service.cs:79`).
- **Every date an event implies is representable**, the day after included
  (`Services/Service.cs:229`).
- **No two non-Deleted events' build-to-strike windows overlap** on an admin save
  (`Services/Service.cs:88`).
- **The clock zone is a known IANA id** (`Services/Service.cs:43`).
- **Every admin write is audited with its actor**: save `Services/Service.cs:114`, make
  active `:161`, timezone `:58`. A request with no resolvable actor is challenged before
  any write (`Controllers/SettingsAdminController.cs:36`, `:53`, `:68`).
- **Every write that may have changed a cycle notifies the listeners, before the audit and
  even when the write throws**: save `Services/Service.cs:106`, make active `:154`,
  timezone `:56`, seeding `:194`, `:207`, `:253`.
- **Only the section writes `settings_event`.** The event writes sit on the internal
  `ISettingsWriteService` (`Services/ISettingsWriteService.cs:8`), off the cross-section
  contract.
- **`/Settings/Admin` is `AdminOnly`, class-level** (`Controllers/SettingsAdminController.cs:15`)
  and has no GET; **`/Settings` is `[Authorize]` only** (`Controllers/SettingsController.cs:7`).
- **A non-admin never sees the list or the form, and `?event=` is ignored for them**
  (`ViewComponents/EventSettingsTabViewComponent.cs:25`).
- **One tab per key, first by weight wins**, and a tab whose `Policy` the viewer fails is
  dropped before its component is invoked (`ViewComponents/SettingsTabComposition.cs:20`,
  `:25`).
- **A saved row stays reachable by id**, deactivated or not
  (`Controllers/SettingsAdminController.cs:45`).

## 5. Seams

- **`SetValueAsync` on the cross-section contract.** Email's and Monitor's flags are
  planned to move into their own sections' settings, after which the key/value write (and
  possibly the store) shrinks or goes. Until then it can also write `Event:TimeZoneId`
  unaudited; nobody does.
- **`Deleted` status.** Reserved for withdrawing a cycle; no screen sets it.
- **Deriving off-season from the strike end.** `IsOffSeason` is an admin checkbox until the
  settings revamp derives it.

## 6. Deliberately not done

- **No blank-form tab for a non-admin.** A viewer who cannot edit and has no active cycle
  gets the empty-state line.
- **No DB uniqueness/check constraint for the one-active rule** — project rule; the service
  guard plus tests are the enforcement.
- **No generic settings-browser UI** over `system_settings`. Each key is owned by its
  writing section; a browse/edit screen would invite hand-editing runtime state.
- **Key/value rows are never deleted** — absence and never-set are the same thing to
  `GetValueAsync`.
- **No resx for the admin list and form** (`memory/code/localization-admin-exempt.md`); the
  read-only rendering an ordinary member sees is localized.
- **No overlap or date check on make-active.** It flips status only; the dates it trusts
  were checked when the row was saved.
- **No re-render of a rejected new-cycle form.** Every refusal is a flash and a redirect;
  the operator retypes (Peter, 2026-09-21).

## Load-bearing weirdness

- **The migrations-history sentinel names `system_settings`** on purpose — the context
  rename also renamed its history table, so the sentinel must name a table that predates
  the pending migrations (`Section.cs` explains).
- **`system_settings` keeps its pre-convention name**; every other table here is
  `settings_*`.
- **The repository is a Singleton** over `IDbContextFactory` (each call opens its own
  context) — deliberate, not a Scoped-service bug.
- **`Year` is derived from the gate-opening date on save** — the form has no Year field.
- **`settings_event.TimeZoneId` is written on save and never read**; reads fill the DTO from
  the global key.
- **`IsOffSeason` is nullable in the entity** because a column added to an existing table
  must be; null reads as false.
- **The tab label lives in `SharedResource`, not this section's resx** — the strip renders
  every contributor's label and cannot see their private resource sets.
- **The tab strip ships an inline script.** Bootstrap ignores the URL fragment, so without
  it every POST redirect lands on the first tab.
- **The Events tab is pinned first** with `Weight = int.MinValue`: every other tab's
  settings hang off the event.
- **`IEventSettingsInfo`'s two clock rules are `sealed` default members** so no implementer
  can override the rule, which also lets the DTO forward to them by name.
- **The seeding seam really deletes rows.** Every other path treats a cycle as permanent;
  `DeleteEventAsync` exists for fixture teardown only.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-08-28 | First doctor pass — section is young (nobodies-collective/Humans#1104) and close to target; drift is at the edges: a Contracts csproj comment naming consumers two moves stale, a dead Google reference, cloned clock-rule comments claiming callers that are not there yet | peterdrier/Humans#1560 |
| 2 | 2026-09-21 | Second pass after the peterdrier/Humans#1628 + nobodies-collective/Humans#1631 rebuild — structure matches target; the drift is the comments the rebuild left behind, all claiming the pre-cutover world | peterdrier/Humans#1778 |
| 3 | 2026-11-11 | The section became the register of every event cycle (list, make-active, global timezone, no overlaps); docs had not followed, the form re-checked rules the save owns, and the read-only tab inverted an empty strike window | peterdrier/Humans#2372 |
