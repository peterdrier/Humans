# Section Align — Events (PR 539)

**Run started:** 2026-05-13 | **Mode:** PR | **Worktree:** `.worktrees/section-align-events/`
**Branch:** `feat/event-guide-code-review` (off `main` @ 89e658e8)
**Phase:** 0 (inventory + decisions locked 2026-05-14)

**Canonical section name proposal:** **`Events`**

---

## Stage 0 — Locked decisions (2026-05-14)

1. **Naming scope:** full rename — C# types, namespace, folders, role, ViewModels, feature flag, CORS policy, DB tables.
2. **Settings-table collision:** new section uses **`event_guide_settings`** (Shifts' `event_settings` is off-limits per the no-touch-other-sections rule).
3. **Entity name:** **`Event`** (singular). Matches the Camps / Teams convention (`Camp`, `Team`, `Campaign`). `CalendarEvent` lives in a different section with no overlapping usage, so the name is safe.
4. **`IEmailService` scope on this PR:** trim the 8 new methods to 2 (1 `Send*` + 1 `Render*` covering all four lifecycle transitions via parameters); defer the full app-wide `IEmailService` → generic `SendAsync(EmailContent)` refactor to [#712](https://github.com/nobodies-collective/Humans/issues/712).
5. **Hitchhikers:** `ShiftSignupService.cs` (CultureInfo fix) and `TicketTransferRepository.cs` (transaction handling) stay in this PR — minor, low-risk, not worth splitting.
6. **Supplier API audit:** complete — see next section.

### Naming convention (locked)

Follows `Camps`/`Camp`/`camp_*` and `Teams`/`Team`/`team_*`:

| Layer | Value |
|-------|-------|
| Section doc / folder / namespace | `Events` (plural) |
| Domain entity | `Event` (singular) |
| DbSet property | `Events` (plural) |
| Master DB table | `events` (plural) |
| Child DB table prefix | `event_*` (singular) |

### Revised DB mapping (locked — singular `event_*` prefix, NOT `events_`)

| Current | Proposed | Note |
|---------|----------|------|
| `event_categories` | `event_categories` | unchanged — already follows convention |
| `guide_events` | `events` | master table, plural |
| `guide_settings` | `event_guide_settings` | dodges Shifts' `event_settings` (off-limits) |
| `guide_shared_venues` | `event_venues` | child, singular prefix; "shared" is impl detail |
| `moderation_actions` | `event_moderation_actions` | child, singular prefix |
| `user_event_favourites` | `event_favourites` | drop `user_` — convention is section prefix only (precedent: `event_participations` doesn't say `user_event_participations`) |
| `user_guide_preferences` | `event_preferences` | same |

### Entity rename map (locked)

| Current | Proposed |
|---------|----------|
| `GuideEvent` | `Event` |
| `GuideSettings` | `EventGuideSettings` (matches table) |
| `EventCategory` | `EventCategory` (unchanged) |
| `GuideSharedVenue` | `EventVenue` |
| `ModerationAction` | `EventModerationAction` |
| `UserEventFavourite` | `EventFavourite` |
| `UserGuidePreference` | `EventPreference` |
| `IEventGuideService` / `EventGuideService` | `IEventService` / `EventService` |
| `IEventGuideRepository` / `EventGuideRepository` | `IEventRepository` / `EventRepository` |
| `GuideEventStatus` | `EventStatus` |
| `ModerationActionType` | `EventModerationActionType` |
| `GuideModerator` (role) | `EventsAdmin` |
| `GuideModeratorOrAdmin` | `EventsAdminOrAdmin` |
| `EventGuideFeatureFilter` | `EventsFeatureFilter` |
| `GuideApi` (CORS policy) | `EventsApi` |
| `Features:EventGuide` (config) | `Features:Events` |

---

## Stage 0 — Supplier API audit results

For each cross-section read we currently do via `.Include` or direct DbSet access, this verifies whether the supplier section exposes a public API we can switch to in Stage 3.

| Outbound access | Supplier | Public API exists? | Gap → action |
|-----------------|----------|---------------------|---------------|
| `.Include(SubmitterUser).ThenInclude(UserEmails)` (display name + email) | Users | ✅ **`IUserService.GetUserInfoAsync(Guid userId, CT)`** returns `UserInfo` with `DisplayName`, `PrimaryEmail`, profile, all 8 contributing tables joined. Cached read-model from PR #521. | **No gap.** Stage 3 switches to this call. |
| `.Include(Camp).ThenInclude(Seasons)` (camp + season context for event listings) | Camps | 🟡 **Probably sufficient** — `ICampService.GetCampsForYearAsync(int year, CT)` returns `CampInfo[]`; `BuildCampDetailDataBySlugAsync` returns season-laden detail. Need to verify the shape matches what the event-listing views actually need before Stage 3. | **Verify in Stage 3.** If gap, follow-up `/section-align` on Camps. |
| `_db.EventSettings.…` (gate opening date, timezone, event name) + `.Include(g => g.EventSettings)` | Shifts/Calendar | ❌ **No supplier service.** `EventSettings` is currently only read via `IShiftSignupRepository` (Shifts' own repo) and the new `IEventGuideRepository.GetActiveEventSettingsAsync` (cross-section violation). No section-level service exposes EventSettings reads. | **CONFIRMED GAP.** Follow-up `/section-align` on Shifts/Calendar required: add `IEventSettingsService.GetActiveAsync()` / `GetByIdAsync(Guid)`. Open as a GitHub issue. Stage 3 documents the gap and leaves Events' cross-section read in place until the supplier ships. |

### Follow-up `/section-align` issues

- **Shifts/Calendar — [#719](https://github.com/nobodies-collective/Humans/issues/719)** — expose `IEventSettingsService` so Events can drop its `_db.EventSettings` reads. Opened 2026-05-14.
- **Camps** (conditional) — if Stage 3 finds `GetCampsForYearAsync` insufficient for the event-listing camp+season projection, open a Camps follow-up. Defer creation until confirmed.

---

Justification: user said it. Already adopted at the URL layer (`/Events/*`, `/api/events/*`, `/Barrios/{slug}/Events/*`). Section doc already named `Events.md`. Everything else in the section needs to follow.

## TL;DR

This is a brand-new section landed in one PR. URLs were renamed to `Events`, but **C# symbols, DB tables, namespaces, folders, role names, ViewModels, DI registration, and feature flag keys still use the old internal names `EventGuide` / `Guide*` / `Moderation`**. The renamed route layer sits on top of unrenamed everything-else, which produces real bugs (a Razor `selected="@(bool)"` family of bugs, a public-API PII leak, missing GDPR contribution, `/Admin/Guide*` URL rule violation) and a large pile of naming drift.

The Codex review bot already filed **8 inline findings**, 6 of which are blocking (Razor boolean attribute, `/Admin/*` route, cross-section EF joins, missing `IUserDataContributor`, public API leaks submitter email, display-name uses `User.Email`). Two are warnings (interface method additions without approval, enum `||`-chain).

On top of those, this audit found:
- DB tables don't share a prefix; **none start with `events_`**.
- Repository takes `HumansDbContext` directly (Scoped) — design rule requires `IDbContextFactory<HumansDbContext>` (Singleton).
- DI registered inline in `Program.cs`, not via `Extensions/Sections/EventsSectionExtensions.cs`.
- Service+repo interface budgets blown (~43 methods each) with no `InterfaceMethodBudgetTests.Budgets` entry.
- Tests scattered in `Repositories/`, `Services/`, `Architecture/` — not under one section folder.
- Role `GuideModerator` does not follow the `*Admin` suffix convention.
- Section doc claims things ("only repo touches our tables", "cross-domain navs only included inside this section") that are technically true today only because nothing else is reading us yet — and is silent on the inbound bot findings (display-name HARD RULE, GDPR contributor, public API PII).

**This PR is not ready for prod.** Phases 1–4 below estimate the work.

---

## Axis 1 — Boundary integrity

### A1.1 Section name consistency — **multi-axis drift**

| Surface | Current name(s) | Canonical proposal |
|---------|-----------------|--------------------|
| Section invariant doc | `docs/sections/Events.md` ✓ | `Events.md` |
| Feature spec | `docs/features/26-events.md`, `27-guide-browser.md` | `26-events.md`; rename `27-events-browser.md`? |
| Web route prefix (UI) | `/Events/*` ✓ | `/Events/*` |
| Web route prefix (Admin) | `/Admin/Guide*` ✗ | `/Events/Admin/*` |
| API route prefix | `/api/events/*` ✓ | `/api/events/*` |
| Service interface | `IEventGuideService` | `IEventsService` |
| Service impl + folder | `Humans.Application.Services.EventGuide.EventGuideService` | `Humans.Application.Services.Events.EventsService` |
| Repository interface | `IEventGuideRepository` | `IEventsRepository` |
| Repository impl + folder | `Humans.Infrastructure.Repositories.EventGuide.EventGuideRepository` | `Humans.Infrastructure.Repositories.Events.EventsRepository` |
| Web controllers | `EventGuideController`, `EventGuideDashboardController`, `EventGuideExportController`, `ModerationController`, `GuideAdminController`, `CampEventsController`, `GuideApiController` | `EventsController`, `EventsDashboardController`, `EventsExportController`, `EventsModerationController`, `EventsAdminController`, `BarrioEventsController` (?), `EventsApiController` |
| Views folder | `Views/EventGuide/`, `Views/EventGuideDashboard/`, `Views/EventGuideExport/`, `Views/Moderation/`, `Views/GuideAdmin/`, `Views/CampEvents/` | `Views/Events/`, `Views/EventsDashboard/`, `Views/EventsExport/`, `Views/EventsModeration/`, `Views/EventsAdmin/`, `Views/BarrioEvents/` |
| ViewModels | `CampEventViewModels.cs`, `DashboardViewModel.cs`, `GuideAdminViewModels.cs`, `GuideApiModels.cs`, `IndividualEventViewModels.cs`, `ModerationViewModels.cs` | All under `Models/Events/` (or `EventsViewModels.cs` + `EventsAdminViewModels.cs` + `EventsApiModels.cs` if we keep flat layout) |
| Role | `GuideModerator` | `EventsAdmin` |
| Role group | `GuideModeratorOrAdmin` | `EventsAdminOrAdmin` |
| Policy name | `GuideModeratorOrAdmin` | `EventsAdminOrAdmin` |
| Feature flag (appsettings) | `Features:EventGuide` | `Features:Events` |
| Filter class | `EventGuideFeatureFilter` | `EventsFeatureFilter` |
| CORS policy | `GuideApi` | `EventsApi` |
| GDPR section constant | (missing) | `GdprExportSections.Events` |
| `InterfaceMethodBudgetTests.Budgets` | (missing — file not even present?) | `IEventsService = 43`, `IEventsRepository = 43` (then trim) |
| Architecture test file | `EventGuideArchitectureTests.cs` | `EventsArchitectureTests.cs` |
| `Extensions/Sections/` | (missing — DI inline in Program.cs) | `EventsSectionExtensions.cs` |
| Entity types | `GuideEvent`, `GuideSettings`, `GuideSharedVenue`, `UserGuidePreference`, `EventCategory`, `ModerationAction`, `UserEventFavourite` | See Concern (entity rename optional — see Stop condition #2) |

**Bot finding 2** confirms `/Admin/Guide*/*` is a HARD-RULE violation (`memory/architecture/no-admin-url-section.md`).

### A1.2 Controller existence — partial

`EventsController.cs` does **not** exist; the analogue is `EventGuideController.cs`. Same for every other controller. Section routes are correctly under `/Events/*` (mostly) but the C# class names lag. No foreign-controller hosting of section routes — every controller for this section is in-section.

**Phase 1 action:** rename the seven controllers. **Run the inbound-link sweep** (per skill Phase 1 doc) on every rename — `asp-controller="EventGuide"`, `Url.Action(..., "EventGuide")`, etc. all need updating. `_Layout.cshtml` alone has 6 `asp-controller="EventGuide…"` and 3 `asp-controller="GuideAdmin"` references.

### A1.3 URL surface — `/Admin/Guide*` is HARD-RULE violation

| Controller | Class-level route | Net effect | Violation? |
|------------|-------------------|------------|------------|
| `EventGuideController` | `/Events` | `/Events/*` | ✓ OK |
| `EventGuideDashboardController` | `/Events/Dashboard` | `/Events/Dashboard/*` | ✓ OK |
| `EventGuideExportController` | `/Events/Export` | `/Events/Export/*` | ✓ OK |
| `ModerationController` | `/Events/Moderate` | `/Events/Moderate/*` | ✓ OK |
| `CampEventsController` | `/Barrios/{slug}/Events` | `/Barrios/{slug}/Events/*` | ✓ OK |
| `GuideApiController` | `/api/events` | `/api/events/*` | ✓ OK |
| `GuideAdminController` | **`/Admin`** with action templates `GuideSettings`, `GuideCategories/*`, `GuideVenues/*` | `/Admin/GuideSettings`, `/Admin/GuideCategories`, `/Admin/GuideVenues`, etc. | **✗ HARD RULE — `architecture_no_admin_url_section`** |

**Fix:** `GuideAdminController` → `EventsAdminController` with `[Route("Events/Admin")]` and action templates `Settings`, `Categories`, `Categories/Create`, `Categories/{id:guid}/Edit`, `Venues`, etc. Update `AdminNavTree.cs:44` accordingly.

### A1.4 Views folder — drift

| Current views folder | Canonical |
|---------------------|-----------|
| `Views/EventGuide/` (Browse, IndividualEventForm, MySubmissions, Schedule) | `Views/Events/` |
| `Views/EventGuideDashboard/` (Index) | `Views/EventsDashboard/` |
| `Views/EventGuideExport/` (Index, PrintGuide) | `Views/EventsExport/` |
| `Views/Moderation/` (Index) | `Views/EventsModeration/` |
| `Views/GuideAdmin/` (GuideCategories, GuideCategoryForm, GuideSettings, GuideVenueForm, GuideVenues) | `Views/EventsAdmin/` |
| `Views/CampEvents/` (Index, CampEventForm) | `Views/BarrioEvents/` (or `Views/Events/Barrio/`) |

### A1.5 ViewModel placement — split / grab-bag drift

Six VM files for one section, none under a section subfolder:

| File | Contents | Canonical proposal |
|------|----------|---------------------|
| `Models/CampEventViewModels.cs` | Barrio event form/list VMs | Move under `Models/Events/` (or merge into `EventsViewModels.cs`) |
| `Models/DashboardViewModel.cs` | **Events dashboard VM, but file name is generic — high collision risk** | Rename to `Models/EventsDashboardViewModel.cs` |
| `Models/GuideAdminViewModels.cs` | GuideAdmin CRUD VMs | Rename to `EventsAdminViewModels.cs` |
| `Models/GuideApiModels.cs` | Public API DTOs | Rename to `EventsApiModels.cs` |
| `Models/IndividualEventViewModels.cs` | EventGuide submission VMs | Rename / move under `Models/Events/` |
| `Models/ModerationViewModels.cs` | Moderation queue VMs | Rename to `EventsModerationViewModels.cs` |

**Particularly bad:** `DashboardViewModel.cs` is a name a future section *will* want.

### A1.6 Controller-base leak

No `HumansControllerBase` additions; controllers each declare their own dependencies. ✓ Clean.

### A1.7 Extensions placement — **DI inline in Program.cs**

Every other section has a `src/Humans.Web/Extensions/Sections/<Section>SectionExtensions.cs` for DI wiring. This section has none — the three lines `AddScoped<EventGuideFeatureFilter>`, `AddScoped<IEventGuideRepository, …>`, `AddScoped<IEventGuideService, …>` are dropped directly into `Program.cs:512–514`. This breaks the convention every other section follows.

**Fix:** create `src/Humans.Web/Extensions/Sections/EventsSectionExtensions.cs` with `AddEventsSection(this IServiceCollection)`, move the three registrations there, call from `Program.cs`.

### A1.8 Role surface — **`GuideModerator` violates `*Admin` suffix convention**

Per `feedback_admin_superset`: domain-scoped roles use `*Admin` suffix (FinanceAdmin, MailerAdmin, TicketAdmin, FeedbackAdmin, NoInfoAdmin, StoreAdmin, etc.). `ConsentCoordinator` and `VolunteerCoordinator` are explicit exceptions where the semantic is "coordinator" not "admin".

`GuideModerator` describes a **section admin** — they approve/reject/manage categories/manage venues/manage settings. Should be `EventsAdmin`.

**18 source references** to migrate when renaming (`RoleNames.cs`, `RoleGroups.cs`, `PolicyNames.cs`, `AuthorizationPolicyExtensions.cs`, `RoleChecks.cs`, `_Layout.cshtml`, all 5 admin/moderation/dashboard/export controllers, `AdminNavTree.cs`, all 4 architecture/auth test files). DB rows in `AspNetRoles` for the role itself need a rename too — should be done via an EF data migration.

### A1.9 Inbound cross-section DB access — **none** (new section, nothing else reads us yet)

Grep across `src/` for `_db.GuideEvents | _db.GuideSettings | _db.EventCategories | _db.GuideSharedVenues | _db.ModerationActions | _db.UserEventFavourites | _db.UserGuidePreferences` returned **zero hits outside the section's own repo + DbContext**. ✓ Clean inbound — we're a producer with no consumers yet.

### A1.10 Inbound EF navigations — none from other sections' entities

Grep `Domain/Entities/*.cs` for `GuideEvent` / `EventCategory` / etc. as navigation property types: zero hits outside the new entity files. ✓ Clean inbound nav.

### A1.11 Outbound cross-section access — **multiple `.Include` chains across into other sections** (HARD violations)

`EventGuideRepository.cs`:

| Line | Code | Section reached | Violation |
|------|------|-----------------|-----------|
| `20` | `_db.GuideSettings.Include(g => g.EventSettings)` | Shifts/Calendar (`EventSettings`) | Cross-domain `.Include` |
| `104, 134, 137, 157, 159–160, 188–189` | `.Include(e => e.Camp!).ThenInclude(c => c.Seasons)` and `.Include(e => e.SubmitterUser).ThenInclude(u => u.UserEmails)` | Camps (`Camp`, `CampSeason`), Users (`User`, `UserEmail`) | Cross-domain `.Include` |
| `23, 29` | `_db.EventSettings.…ToListAsync(ct)` / `_db.EventSettings.FindAsync([id], ct)` | Shifts/Calendar | **Outbound DbSet read** of another section's table |

Plus the configuration-side bot finding (A1 / bot finding 3): `GuideEventConfiguration.HasOne(e => e.Camp)`, `HasOne(e => e.SubmitterUser)`, `ModerationActionConfiguration.HasOne(m => m.ActorUser)`, `UserEventFavouriteConfiguration.HasOne(f => f.User)`, `UserGuidePreferenceConfiguration.HasOne(p => p.User)`, `GuideSettingsConfiguration.HasOne(g => g.EventSettings)` — all create cross-section FK constraints that the rule `memory/architecture/no-cross-section-ef-joins.md` forbids even with `OnDelete(Restrict)`.

**Boundary-fix protocol** for each (consumer side — we are reaching out):

| Outbound access | Supplier section | Supplier API exists? | Disposition |
|-----------------|------------------|----------------------|--------------|
| `Include(Camp).ThenInclude(Seasons)` | Camps | Need to check (`ICampService` / `ICampReadService`) | If yes → switch in Phase 2. If no → flag Camps as follow-up `/section-align` target. |
| `Include(SubmitterUser).ThenInclude(UserEmails)` | Users | `IUserInfoService` has cached read-model (`UserInfo`) per commit 478b22b3 | Switch the display-name lookup to `IUserInfoService.GetByIdAsync(userId)` in Phase 2. |
| `_db.EventSettings.ToListAsync` / `FindAsync` | Shifts/Calendar | Need to check (`IEventSettingsService`?) | If yes → switch. If no → flag Shifts as follow-up. |
| `Include(g => g.EventSettings)` on GuideSettings | Shifts/Calendar | Need (e.g.) `IEventSettingsService.GetByIdAsync` | If yes → load `EventSettings` separately and stitch; if no → follow-up. |
| `HasOne(Camp)` / `HasOne(SubmitterUser)` / `HasOne(ActorUser)` etc. in EF configs | n/a — these are config-side | n/a | **Phase 2 fix**: drop `HasOne(...)` to other-section entities, keep only the bare FK column. Remove navigation properties on `GuideEvent`, `ModerationAction`, `UserEventFavourite`, `UserGuidePreference`, `GuideSettings` for cross-section types. Use service-layer joins. |

### A1.12 Controller → DbContext

Zero controllers inject `HumansDbContext`. ✓ §2a satisfied.

### A1.13 Migrations — auto-generated but contains seed data

`20260513151029_AddEventGuide.cs` is EF-output shape (good — PR body confirms it was regenerated). Has `InsertData` calls at line 198 seeding 6 default categories. Acceptable; seed data via `HasData` is standard EF, no hand-edits to migration body. **No Designer.cs concerns** — designer exists.

However, the migration name (`AddEventGuide`) and the table names (`guide_events`, `guide_settings`, …) lock in the old naming. If we rename C# entities, the rename also generates a follow-up migration. See A1.14 (table mapping).

### A1.14 — **DB table mapping** (user-requested headline section)

Current state — **none of the 7 new tables start with `events_`**:

| Current table | Prefix today | Owner | Used in C# as |
|---------------|--------------|-------|---------------|
| `event_categories` | `event_` (singular) | new Events section | `_db.EventCategories` |
| `guide_events` | `guide_` | new Events section | `_db.GuideEvents` |
| `guide_settings` | `guide_` | new Events section | `_db.GuideSettings` |
| `guide_shared_venues` | `guide_` | new Events section | `_db.GuideSharedVenues` |
| `moderation_actions` | `moderation_` (no prefix at all) | new Events section | `_db.ModerationActions` |
| `user_event_favourites` | `user_event_` | new Events section | `_db.UserEventFavourites` |
| `user_guide_preferences` | `user_guide_` | new Events section | `_db.UserGuidePreferences` |

**Adjacent tables that already exist** and constrain the rename:

| Existing table | Owner section |
|----------------|---------------|
| `event_settings` | Shifts/Calendar (`EventSettings` — gate opening, timezone, event name) |
| `event_participations` | Shifts |
| `calendar_events` | Calendar |
| `calendar_event_exceptions` | Calendar |

**The collision:** the new section's `guide_settings` is conceptually "the Events section's submission-window configuration" — adjacent to but distinct from Shifts' `event_settings` (the broader festival event configuration). Both genuinely concern an "event" but at different levels of abstraction. We cannot have two `event_settings`/`events_settings` tables.

**Proposed mapping** (recommended):

| Current | Proposed | Notes |
|---------|----------|-------|
| `event_categories` | **`events_categories`** | Drop singular `event_`, adopt section-prefix `events_`. |
| `guide_events` | **`events`** | This IS the events table. No `events_` self-prefix needed for the central entity. |
| `guide_settings` | **`events_settings`** | ⚠ Resolves the collision by claiming `events_settings` for the Events section and keeping `event_settings` (singular) for Shifts/Calendar. Confusing — humans will mis-type. **Alternative: `events_guide_settings`** (verbose but unambiguous). |
| `guide_shared_venues` | **`events_venues`** | "Shared" is implementation detail; venue = venue. |
| `moderation_actions` | **`events_moderation_actions`** | Add `events_` prefix; keep `moderation_actions` semantic. |
| `user_event_favourites` | **`events_user_favourites`** | Re-prefix from `user_event_` → `events_user_`. |
| `user_guide_preferences` | **`events_user_preferences`** | Re-prefix. |

**Strategic question for user (Stop condition #3, below):** how to resolve the `event_settings` vs `events_settings` collision. Three options:

1. Rename Shifts/Calendar `event_settings` to `calendar_settings` or `festival_settings` — clean future-proof but expands the blast radius (Shifts section is established).
2. Use `events_guide_settings` (verbose, unambiguous, slightly stutter-y) for new Events section.
3. Accept `events_settings` (plural) alongside `event_settings` (singular) — high human-error risk.

**Rename mechanics:**
- New migration `RenameEventsTables` that does `RenameTable("guide_events", "events")` etc. via `migrationBuilder.RenameTable`. Indexes auto-rename. FK constraint names need explicit `RenameIndex`/regen.
- Per `architecture_no_drops_until_prod_verified`: the section is **not in prod yet** (PR 539 hasn't been merged to upstream), so a clean rename is safe — no prod data to preserve.
- **Verify** with `dotnet ef migrations remove` followed by clean `dotnet ef migrations add RenameEventsTables` after the `[Table("…")]` attribute / `ToTable(...)` calls are updated.

### A1.15 Open prior-review items (PR mode)

8 inline findings from claude-bot @ commit 89e658e8 — 6 BLOCK, 2 WARN. All unresolved. Cross-referenced by axis where relevant.

| Finding | Path | Line | Type | Tracked under |
|---------|------|------|------|----------------|
| Razor `selected="@(bool)"` bug | `Views/CampEvents/CampEventForm.cshtml` | 61 | BLOCK | Bug-fix (see Bug list) |
| `/Admin/Guide*` route violates `architecture_no_admin_url_section` | `Controllers/GuideAdminController.cs` | 14 | BLOCK | A1.3 |
| Cross-section EF `HasOne` constraints | `Configurations/GuideEventConfiguration.cs` | 30 | BLOCK | A1.11 |
| Missing `IUserDataContributor` for `user_event_favourites` + `user_guide_preferences` | `Services/EventGuide/EventGuideService.cs` | 12 | BLOCK | A2 (new — see below) |
| Public API leaks submitter email + display-name HARD RULE | `Controllers/Api/GuideApiController.cs` | 55 | BLOCK | Bug-fix + display-name HARD RULE |
| Display name uses `User.Email` instead of `Profile.BurnerName` | `Controllers/ModerationController.cs` | 155 | BLOCK | Bug-fix + display-name HARD RULE |
| 4 new methods added to `IEmailService` + 4 to `IEmailRenderer` without approval | `Interfaces/Email/IEmailService.cs` | 320 | WARN | A2 (interface budget) |
| Enum `||`-chain on `HasConversion<string>()` field | `Repositories/EventGuide/EventGuideRepository.cs` | 175 | WARN | Bug-fix |

All 8 are unresolved as of this inventory. Phase 2 must thread-reply each.

### Bug-fix list (extracted from review + own audit)

Pre-prod blockers beyond the rename mechanics:

1. **Razor `selected="@(bool)"` bug — 10 sites** found in this section's views (some use the safe ternary form, some don't):
   - `Views/CampEvents/CampEventForm.cshtml:61, 74, 94` — **bug** (`@(bool)` form, missing ternary)
   - `Views/GuideAdmin/GuideSettings.cshtml:25` — safe (ternary form)
   - `Views/EventGuide/Browse.cshtml:45, 55` — safe (ternary)
   - `Views/EventGuide/IndividualEventForm.cshtml:58, 70, 88, 108` — safe (ternary)
   - **3 bug sites** to fix in `CampEventForm.cshtml`.
2. **Display name HARD RULE violations** — `BurnerName`, not `Email`:
   - `ModerationController.cs:155` (`guideEvent.SubmitterUser.Email ?? "Unknown"`).
   - `GuideApiController.cs:55` (DTO ships `SubmitterUser.Email` to anonymous callers — also a PII leak).
3. **Public API PII leak** — `GET /api/events/events` exposes submitter email to unauthenticated callers via the CORS-open `GuideApi` policy. Must strip the field; if a display name is needed, use `Profile.BurnerName` via `IUserInfoService`.
4. **Enum `||`-chain** in `EventGuideRepository.cs:175` — switch to `.Contains()` with explicit allowed-values list (`memory/code/no-enum-compare-in-ef.md`).
5. **Cross-section `.Include` chains** (A1.11) — break and use service-layer joins.
6. **`/Admin/Guide*` route** (A1.3) — move under `/Events/Admin/*`.

---

## Axis 2 — Internal cohesion

### A2.1 EF leakage from service — **clean**

`EventGuideService.cs` — no `Microsoft.EntityFrameworkCore` import, no `IQueryable` / `DbContext` / `.Include` / `.AsNoTracking`. Calls `_repo.SaveChangesAsync(ct)` but `SaveChangesAsync` is on `IRepository`-marker (the repo abstraction handles this). ✓ Section's service is properly insulated.

**Add architecture test:** `EventGuideService_HasNoDbContextConstructorParameter` exists ✓ — but the generic `Application_Services_TakeNoDbContext` reflection test (A2.7) would obsolete it.

### A2.2 Caching placement — **none** (acceptable)

Section doc explicitly opts out of caching (small dataset, moderated content, staleness risk). Grep across `src/Humans.Application/Services/EventGuide/`, `src/Humans.Infrastructure/Repositories/EventGuide/`, the 7 controllers, and ViewComponents: zero `IMemoryCache` / `IDistributedCache` / `_cache` references. ✓ Compliant with §15 (no caching layer rather than the wrong caching layer).

### A2.3 DI lifetimes — **wrong lifetime for repo + inline in Program.cs**

Current (`Program.cs:512–514`):

```csharp
builder.Services.AddScoped<EventGuideFeatureFilter>();                          // OK
builder.Services.AddScoped<IEventGuideRepository, EventGuideRepository>();      // ✗ should be Singleton + factory
builder.Services.AddScoped<IEventGuideService, EventGuideService>();            // OK (Scoped)
```

Per design rules + camp/section gold standard:

- Repository should be **`AddSingleton`** depending on **`IDbContextFactory<HumansDbContext>`** (not `HumansDbContext`).
- Repository ctor must take `IDbContextFactory<HumansDbContext>`, opening a per-method `using var ctx = factory.CreateDbContext()`.
- Filter Scoped is correct.
- Service Scoped is correct (or Singleton if all deps are Singleton).

**Phase 2 fix:** convert `EventGuideRepository(HumansDbContext db)` to `EventGuideRepository(IDbContextFactory<HumansDbContext> factory)`, refactor every method to open + dispose a context. Then change `AddScoped` → `AddSingleton` for the repository. Move all three registrations into `Extensions/Sections/EventsSectionExtensions.cs`.

### A2.4 Repository pattern

`EventGuideRepository`:

- ✓ `sealed`
- ✗ uses `HumansDbContext` directly, not `IDbContextFactory<HumansDbContext>` (see A2.3)
- ✓ `IEventGuideRepository : IRepository` marker present
- ✗ Lives at `Humans.Infrastructure/Repositories/EventGuide/` — namespace will be renamed `Events` per A1.1
- ✓ Has Add/Update/Remove paths; for `ModerationAction` we need to verify append-only (no Update / Remove on the moderation actions table). Spot-check:

```bash
grep -nE 'ModerationAction|Update|Remove' src/Humans.Infrastructure/Repositories/EventGuide/EventGuideRepository.cs
```

— `Add(ModerationAction action)` is exposed; no Update / Remove method exists on `ModerationAction`. ✓ Append-only shape compatible with section doc invariant. Should add an architecture test pinning this.

### A2.5 Shared visual components — **no ViewComponents created**; many candidate sites

Grep for new ViewComponents in this PR: zero. Section renders ~5 distinct surfaces — Browse list, MySubmissions list, Moderation queue, Dashboard, Schedule — each inline in its view.

| Candidate | Should be |
|-----------|-----------|
| Event card / row rendering (Browse, MySubmissions, Schedule, Moderation, Print Guide all roll their own) | `<vc:event-card>` ViewComponent |
| Day-toggle button group (Browse) | TagHelper or partial — judgment call |
| Status badge ("Pending", "Approved", "Rejected", "ResubmitRequested", "Withdrawn") | Either a small TagHelper or use `_StatusBadge.cshtml` if it exists |

**A2.5a Redundancy vs system-level shared components — HIGH:**

| Found in this section | Should use canonical |
|-----------------------|----------------------|
| `Views/CampEvents/CampEventForm.cshtml` inlines a submitter display via `@user.DisplayName` | `<vc:human user-id="…" />` |
| `Views/Moderation/Index.cshtml` likely shows submitter names inline (verify in Phase 0 closing) | `<vc:human>` |
| `Views/EventGuide/MySubmissions.cshtml` — own user shown, likely OK; verify | — |
| Submitter email in API DTO (HARD RULE) | strip; use `IUserInfoService.GetByIdAsync(...).DisplayName` server-side |
| `Views/EventGuideExport/PrintGuide.cshtml` likely lists submitters with custom markup | `<vc:human compact="true">` or whatever the canonical print-shape is |

Need a Phase 0 verification grep across this section's views for `DisplayName | Email | .*ProfileLink` patterns; the inline form needs to be the canonical `<vc:human>` family per the user-display table in `section-align.md` A2.5a.

### A2.6 Interface budget + segregation — **wildly over budget, no `Budgets` entry**

| Interface | Method count | Budget entry exists? |
|-----------|--------------|----------------------|
| `IEventGuideService` | ~43 | **No `InterfaceMethodBudgetTests` file or `Budgets` entry exists in this repo.** |
| `IEventGuideRepository` | ~43 | No entry |
| `IEmailService` | +4 new (bot finding 7) | **Existing interface — requires explicit approval per `interface-method-additions-are-debt`** |
| `IEmailRenderer` | +4 new (bot finding 7) | Same |

Phase 2 actions:

1. **Find/create `InterfaceMethodBudgetTests.Budgets` registry** — search this commit for `InterfaceMethodBudget*`; if absent, the codebase's ratchet is informal. Add explicit ratchet pinning at current counts for `IEventsService` and `IEventsRepository` so they can only go down.
2. **Trim before pinning.** Both interfaces have multiple status-split methods that could collapse to `GetAll()` + caller-side filter at ~500-user scale. Examples (from repo grep): `GetActiveCategoriesAsync`, `GetAllCategoriesAsync`, `GetCategoryAsync`, `GetCategoryWithEventsAsync`, `GetCategoryBySlugAsync`, `GetMaxCategoryDisplayOrderAsync`, `GetAllCategoriesOrderedAsync`, `Add`, `Remove`. The seven category methods can likely collapse to 2–3.
3. **Push back on 4 new `IEmailService` methods** — the rule explicitly bans this. Either consolidate to a single `SendEventModerationNotificationAsync(GuideEvent, ModerationActionType, string reason)` or document Peter's prior approval in the PR body and the section doc.
4. **`OutboxEmailService` `NotSupportedException` smell** (PR body): four of the new methods throw `NotSupportedException` on the outbox path because the bot review's `interface-method-additions-are-debt` is bumping against the section's design. Cleanly: trim to one method.

### A2.7 Architecture test coverage — **mostly present, prefer generic over per-section**

Current `EventGuideArchitectureTests.cs` has 7 tests:

- ✓ `EventGuideService_LivesInHumansApplicationServicesEventGuideNamespace` — should generalize to `Application_Services_LiveInExpectedNamespaces`.
- ✓ `EventGuideService_HasNoDbContextConstructorParameter` — should generalize to `Application_Services_TakeNoDbContext`.
- ✓ `EventGuideService_TakesRepositoryInterface` — section-specific (keep, or generalize as `Application_Services_TakeRepositoryInterface` if all do).
- ✓ `IEventGuideService_LivesInApplicationInterfacesEventGuideNamespace` — should generalize.
- ✓ `IEventGuideRepository_LivesInApplicationInterfacesRepositoriesNamespace` — should generalize to `IRepository_LivesInExpectedNamespace`.
- ✓ `EventGuideRepository_IsSealedAndImplementsRepositoryInterface` — should generalize to `IRepository_Implementations_AreAllSealed`.
- ✓ `EventGuideRoutes_UseEventsAndBarriosSlugs` — section-specific, **but should also pin `EventsAdminController.Route == "Events/Admin"`** (currently doesn't because `GuideAdminController` is excluded).
- ✓ `EventGuideRoutes_DoNotExposeOldEventGuideOrCampsSlugs` — section-specific, also misses `GuideAdminController` (which has `[Route("Admin")]`).

**Missing arch tests** that should be added (section-specific where they can't generalize):

- **`Only<Events>Repository_References_<DbSet>`** for the 7 new DbSets — proves single-writer rule. Roslyn or reflection scan.
- **`ModerationActionRepository_HasNoUpdateOrDeleteMethods`** — append-only invariant.
- **`EventsAdminController_LivesUnderEventsAdminRoute`** — pin the rule about `/Events/Admin/*` after rename.
- **`EventsService_ImplementsIUserDataContributor`** — once the bot finding 4 is fixed, pin it.

**Per-section duplicates to consolidate (Phase 3 candidate, not blocker):** if there isn't already a generic `IRepository_Implementations_AreAllSealed` or `Application_Services_TakeNoDbContext` test in `Architecture/Rules/`, prefer adding generic tests once the rename is complete and removing the per-section copies.

### A2 cross-cutting bug (bot finding 4): missing GDPR contributor

The section owns two per-user tables: `user_event_favourites` and `user_guide_preferences`. Per §8a, the owning service must implement `IUserDataContributor` so GDPR export and right-to-deletion sweep these rows. Currently:

- `EventGuideService` does NOT implement `IUserDataContributor`.
- `GdprExportSections` has no `Events` (or equivalent) constant.
- DI registration is not forwarded via the section's contributor pattern.

**Phase 2 fix is non-trivial:**
1. Add `GdprExportSections.Events = "Events"` constant.
2. `EventsService : IEventsService, IUserDataContributor` with `BuildExportAsync(userId, ct)` returning `user_event_favourites` + `user_guide_preferences` slices.
3. DI forwarding: `services.AddScoped<EventsService>(); services.AddScoped<IEventsService>(sp => sp.GetRequiredService<EventsService>()); services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<EventsService>());`
4. Tests for both contribution and deletion sweep.

---

## Axis 3 — Test focus

### A3.1 Test folder placement — **scattered across 3 folders, none under section**

Current layout:

```
tests/Humans.Application.Tests/Services/EventGuideServiceTests.cs        (drift)
tests/Humans.Application.Tests/Repositories/EventGuideRepositoryTests.cs  (drift)
tests/Humans.Application.Tests/Architecture/EventGuideArchitectureTests.cs (correct — Architecture is its own category)
tests/Humans.Domain.Tests/Entities/GuideEventTests.cs                     (correct — Domain entity test)
```

Per skill canon: `tests/Humans.Application.Tests/Events/` with `EventsServiceTests.cs` + `EventsRepositoryTests.cs` (plus any other class-specific tests). Architecture and Domain tests stay where they are.

**Phase 1 mechanical move:** `git mv` the two scattered files into `tests/Humans.Application.Tests/Events/`, rename to drop `Guide` prefix.

### A3.1a One test file per production class

| Production class | Test file | OK? |
|------------------|-----------|-----|
| `EventGuideService` | `EventGuideServiceTests.cs` | ✓ |
| `EventGuideRepository` | `EventGuideRepositoryTests.cs` | ✓ |
| `GuideEvent` (entity) | `GuideEventTests.cs` (Domain) | ✓ |
| `EventCategory` (entity) | none | ⚠ acceptable — pure DTO-shaped entity, no behavior |
| `GuideSettings` (entity) | none | ⚠ same |
| `ModerationAction` (entity) | none | ⚠ same |
| `GuideSharedVenue` (entity) | none | ⚠ same |
| `UserEventFavourite`, `UserGuidePreference` (entities) | none | ⚠ same |
| `OutboxEmailService` (4 new methods) | `OutboxEmailServiceTests.cs` exists (was modified) — verify coverage of new throwing methods | check Phase 0 closing |
| `EventGuideFeatureFilter` | none | ⚠ missing — should have a test that 404s when flag off |
| `EmailRenderer` (4 new methods) | needs verification | check |
| `SmtpEmailService` / `StubEmailService` (4 new methods) | needs verification | check |

### A3.2 Coverage map vs section doc

Reading `docs/sections/Events.md`:

| Item from section doc | Test exists? |
|-----------------------|--------------|
| Invariant: "Submissions only accepted when `now >= SubmissionOpenAt && now <= SubmissionCloseAt`" | needs verification in `EventGuideServiceTests` |
| Invariant: "Moderation action may only be applied to a Pending event" | needs verification |
| Invariant: "ModerationAction records are never deleted or updated" | architecture test missing |
| Invariant: "Category slugs are globally unique" | needs verification |
| Invariant: "Public API gated by `EventGuideFeatureFilter` + CORS open" | filter test missing; CORS pinned by inspection only |
| Invariant: "Excluded category slugs validated against active categories" | needs verification |
| Invariant: "StartAt always UTC Instant" | covered by `GuideEventTests.cs` (presumably) |
| Negative: "Non-moderators cannot approve, reject, request edits" | covered by `EndpointAuthorizationTests.cs` (touched) — verify |
| Negative: "Non-moderators cannot create/edit/delete categories or venues or settings" | same |
| Negative: "Submitter cannot moderate own event" | needs verification |
| Negative: "Public API cannot return unapproved events" | needs new test |
| Negative: "Favourites + preferences cross-origin denied" | needs new test |
| Negative: "Barrio events cannot be submitted via `EventGuideController`" | needs verification |
| Trigger: "Moderation action → email sent" | needs verification (likely uses NSubstitute on `IEmailService` — watch for `Task<string>` empty trap, `feedback_nsubstitute_task_string_empty`) |
| Trigger: "Approve → status transitions + ModerationAction record" | needs verification |

Verification pass against the actual test files is Phase 0 closing work — flagged for the orchestrator session.

### A3.3 Redundancy / over-testing — TBD

Not assessed in this Phase 0 pass; can only meaningfully evaluate after we've read the actual test bodies. Flag for closing /section-align audit.

### A3.4 Test-to-section ratio — sanity check

Production LOC (this section's surface area):
```
src/Humans.Application/Services/EventGuide/EventGuideService.cs                359
src/Humans.Infrastructure/Repositories/EventGuide/EventGuideRepository.cs      261
src/Humans.Domain/Entities/{7 new entities}                                    ~500 (est)
src/Humans.Web/Controllers/{7 controllers}                                    ~2,082
src/Humans.Web/Models/{6 VM files}                                            ~? (est ~600)
src/Humans.Web/Views/{17 cshtml}                                              ~? (est ~1,500)
Total                                                                          ~5,300
```

Test LOC (this section's tests):
```
tests/Humans.Application.Tests/Services/EventGuideServiceTests.cs            (need wc)
tests/Humans.Application.Tests/Repositories/EventGuideRepositoryTests.cs     (need wc)
tests/Humans.Application.Tests/Architecture/EventGuideArchitectureTests.cs   ~113
tests/Humans.Domain.Tests/Entities/GuideEventTests.cs                        (need wc)
```

Rough order-of-magnitude — probably **under-tested** given the section is ~5k LOC with multiple invariants/negatives/triggers from the section doc not obviously covered. Phase 2 will add invariant/negative/trigger coverage.

### A3.5 Brittleness signals — TBD

Not assessed in this Phase 0 pass; defer to Phase 3.

### A3.6 Mutation signal (Stryker.NET) — no recent report

No `local/stryker-runs/events/` directory. Section is new; a baseline Stryker run before Phase 3 pruning would help. Surface to user as optional Phase 2 / pre-Phase-3 step.

---

## Stop conditions tripped — **user decisions needed before Phase 1**

### Stop 1 — **Strategic naming scope: rename C# / DB / role / namespace, or only the URL layer?**

The 2026-05-03 route-renaming plan explicitly took option **(b)** — rename URLs only, keep internal names. The user's command today implies option **(a)** — also rename DB tables, role, and asks about admin role name. There are three valid stances:

- **Option A (full rename — recommended):** rename **everything** — C# entity, service, repo, controllers, views, ViewModels, namespace, folder, DB tables, role, filter, feature flag, CORS policy — to canonical `Events`. Largest scope; one cohesive pass; canonical end state. Aligns with the section-align skill's definition of "aligned". Largest diff and largest review surface.
- **Option B (DB + role only):** keep C# type names (`GuideEvent`, `EventGuideService`, etc.) and folder names, but **rename DB tables** to the `events_` prefix and **rename the role** `GuideModerator → EventsAdmin`. Smaller scope, but leaves "EventGuide" everywhere in code.
- **Option C (status quo + bug-fix only):** ship Phase 0 fixes (bot blockers + bugs) and defer naming alignment to a follow-up PR. Lowest risk for this PR; highest tech-debt carry.

**Recommendation: A.** The section isn't in prod yet (`Features:EventGuide` is set to `true` in appsettings but the PR hasn't been merged to upstream). The "free" window to rename closes the moment we promote.

### Stop 2 — `event_settings` vs `events_settings` collision

If Stop 1 chooses A or B (any DB rename), the existing Shifts/Calendar table `event_settings` collides with what would be the natural new name `events_settings`. Three options:

1. **Rename `event_settings` → `calendar_settings`** in Shifts/Calendar. Cleanest long-term; expands the blast radius slightly.
2. **Use `events_guide_settings`** (verbose, unambiguous, slightly stutter-y) for the new Events section.
3. **Accept `events_settings` (plural) alongside `event_settings` (singular)** — high human-error risk; not recommended.

### Stop 3 — Cross-section API gaps (boundary-fix protocol)

The repository's outbound `.Include` and `_db.EventSettings` reads (A1.11) require switching to public service APIs on Camps, Users (`IUserInfoService` likely sufficient), and Shifts/Calendar. **Need to audit each supplier section for the needed read method** before Phase 2 can land all of these. Some may need new methods on supplier-side services — those bumps trigger their own follow-up `/section-align` targets.

### Stop 4 — Approval for new `IEmailService` / `IEmailRenderer` methods

Bot WARN finding: 8 methods total added across two interfaces. `interface-method-additions-are-debt` says stop and ask. Either:

- Trim to a single `SendEventModerationNotificationAsync(...)` per interface (2 methods total instead of 8), or
- Document Peter's prior approval in the PR body and the section doc justification under `## Architecture`.

---

## Follow-up `/section-align` targets

(Documented now; created only after Phase 2 confirms which suppliers actually need surface bumps.)

| Section | Why | Likely surface required |
|---------|-----|--------------------------|
| Camps | We `Include(e => e.Camp).ThenInclude(c => c.Seasons)` from `GuideEvent` | `ICampService.GetByIdsWithSeasonsAsync(IEnumerable<Guid> ids)` or similar |
| Users (via UserInfo) | Display-name lookup (HARD RULE) | likely already covered by `IUserInfoService.GetByIdAsync(userId)` from PR #521 — verify |
| Shifts / Calendar | We read `_db.EventSettings` and `Include(g => g.EventSettings)` on `GuideSettings` | `IEventSettingsService.GetByIdAsync` + a list method |

---

## Stage detail (post-Stage-0)

### Tooling discipline (every stage)

- **`reforge` first, grep second.** For any C# symbol rename / move / interface change, run reforge to enumerate callers, references, implementations. Reforge sees what grep can't (interface dispatch, `nameof()`, attributes, LINQ expression trees). Only after the reforge edits land do you run the **inbound-link sweep** (the 7-bucket grep in the section-align skill's Phase 1 doc) for string-typed references reforge can't see (Razor `asp-controller=`, `Url.Action`, allow-lists, config keys, doc references).
- **Build green between commits.** `dotnet build Humans.slnx -v quiet` after each rename group. If a rename group fails build mid-way, fix forward — don't roll back the group.
- **One rename group per commit.** Reviewers (Codex + Claude) re-review each push; smaller commits = better signal.

### Stage 1 — Bot blockers, rename-independent

4 fixes, can land in 1–2 commits:

1. **Razor `selected="@(bool)"` bug** — `Views/CampEvents/CampEventForm.cshtml:61, 74, 94`. Change `selected="@(x == y)"` → `selected="@(x == y ? "selected" : null)"`. (The other 7 sites in this section already use the safe form.)
2. **Display-name HARD RULE + PII leak** — `ModerationController.cs:155`, `GuideApiController.cs:55`. Switch `submitterUser.Email` (used both as display name and shipped in anonymous DTO) to `IUserService.GetUserInfoAsync(userId)` → `userInfo.DisplayName`. Strip `Email` from the public-API DTO entirely (no replacement; submitters aren't named publicly).
3. **Enum `||`-chain** — `EventGuideRepository.cs:175`. Switch chained equality to `new[] { … }.Contains(e.Status)`.

Thread-reply bot findings 1, 5, 6, 8 on push.

### Stage 2 — The big rename (collision-safe order)

Each numbered group is one commit. Build green at every step. Reforge commands inline.

**2.1 — Domain entities + enums (leaves first).** Nothing in the section depends on these renames except the rest of the section.

```
reforge references GuideEvent
reforge references GuideSettings
reforge references GuideSharedVenue
reforge references ModerationAction
reforge references UserEventFavourite
reforge references UserGuidePreference
reforge references GuideEventStatus
reforge references ModerationActionType
```

Renames per the locked entity map:
- `GuideEvent` → `Event`
- `GuideSettings` → `EventGuideSettings`
- `GuideSharedVenue` → `EventVenue`
- `ModerationAction` → `EventModerationAction`
- `UserEventFavourite` → `EventFavourite`
- `UserGuidePreference` → `EventPreference`
- `GuideEventStatus` → `EventStatus`
- `ModerationActionType` → `EventModerationActionType`
- `EventCategory` — unchanged

Includes file renames (`Domain/Entities/GuideEvent.cs` → `Event.cs`) via `git mv` and matching `using`-statement updates.

**2.2 — DbSet properties + `ToTable` / `[Table]` attributes.**

Edit `HumansDbContext.cs:70–76`:
```csharp
public DbSet<Event> Events => Set<Event>();
public DbSet<EventGuideSettings> EventGuideSettings => Set<EventGuideSettings>();
public DbSet<EventCategory> EventCategories => Set<EventCategory>();
public DbSet<EventVenue> EventVenues => Set<EventVenue>();
public DbSet<EventModerationAction> EventModerationActions => Set<EventModerationAction>();
public DbSet<EventFavourite> EventFavourites => Set<EventFavourite>();
public DbSet<EventPreference> EventPreferences => Set<EventPreference>();
```

Update `ToTable(...)` in all 7 entity configurations to match the locked DB table map.

**Do NOT regenerate the migration yet** — that happens in Stage 4 after cross-section navs are stripped (Stage 3) so the regen captures everything in one file.

**2.3 — Repository (interface + impl + namespace + folder).**

```
reforge references IEventGuideRepository
reforge references EventGuideRepository
```

- `IEventGuideRepository` → `IEventRepository`
- `EventGuideRepository` → `EventRepository`
- `git mv` `src/Humans.Application/Interfaces/Repositories/IEventGuideRepository.cs` → `IEventRepository.cs`
- `git mv` `src/Humans.Infrastructure/Repositories/EventGuide/` → `Events/`
- Namespace `Humans.Infrastructure.Repositories.EventGuide` → `Humans.Infrastructure.Repositories.Events`

**2.4 — Service (interface + impl + namespace + folder).**

```
reforge references IEventGuideService
reforge references EventGuideService
```

- `IEventGuideService` → `IEventService`
- `EventGuideService` → `EventService`
- `git mv` `src/Humans.Application/Interfaces/EventGuide/` → `Events/`
- `git mv` `src/Humans.Application/Services/EventGuide/` → `Events/`
- Namespaces follow

**2.5 — ViewModels.**

Consolidate the 6 grab-bag files under `Models/Events/`:
- `Models/CampEventViewModels.cs` → `Models/Events/BarrioEventViewModels.cs`
- `Models/DashboardViewModel.cs` → `Models/Events/EventsDashboardViewModel.cs`
- `Models/GuideAdminViewModels.cs` → `Models/Events/EventsAdminViewModels.cs`
- `Models/GuideApiModels.cs` → `Models/Events/EventsApiModels.cs`
- `Models/IndividualEventViewModels.cs` → `Models/Events/IndividualEventViewModels.cs`
- `Models/ModerationViewModels.cs` → `Models/Events/EventsModerationViewModels.cs`

```
reforge references DashboardViewModel    # confirm this is only used by the events dashboard before rename
```

(Don't rename `DashboardViewModel` if it's actually shared — but per the file's existing scope it's events-only.)

**2.6 — Controllers (class names + Route attributes).**

```
reforge references EventGuideController
reforge references EventGuideDashboardController
reforge references EventGuideExportController
reforge references ModerationController
reforge references GuideAdminController
reforge references CampEventsController
reforge references GuideApiController
```

| Old class | New class | Old route | New route |
|-----------|-----------|-----------|-----------|
| `EventGuideController` | `EventsController` | `/Events` | `/Events` (unchanged) |
| `EventGuideDashboardController` | `EventsDashboardController` | `/Events/Dashboard` | unchanged |
| `EventGuideExportController` | `EventsExportController` | `/Events/Export` | unchanged |
| `ModerationController` | `EventsModerationController` | `/Events/Moderate` | unchanged |
| `GuideAdminController` | `EventsAdminController` | `/Admin` ⚠ | **`/Events/Admin`** (fixes A1.3) |
| `CampEventsController` | `BarrioEventsController` | `/Barrios/{slug}/Events` | unchanged |
| `GuideApiController` | `EventsApiController` | `/api/events` | unchanged |

**`GuideAdminController` is the special case** — also rewrite its action-level templates from `[HttpGet("GuideSettings")]`, `[HttpGet("GuideCategories")]`, `[HttpGet("GuideVenues")]` etc. to `Settings`, `Categories`, `Venues`. 18 action templates to update.

**After this commit: run the inbound-link sweep** (7 buckets per skill Phase 1 doc) for all 7 old controller names. Critical targets:
- `_Layout.cshtml` lines 111, 112, 113, 118, 119, 120, 126, 127, 128 (`asp-controller="EventGuide*"`, `asp-controller="GuideAdmin"`, `asp-controller="Moderation"`).
- `AdminNavTree.cs:44` (`"GuideAdmin"`, `"GuideSettings"` strings).
- Razor URL helpers in section's own views (`asp-controller="CampEvents"`, etc.).
- Test fixtures pinning routes.

**2.7 — Views folders.**

`git mv` each:
- `Views/EventGuide/` → `Views/Events/`
- `Views/EventGuideDashboard/` → `Views/EventsDashboard/`
- `Views/EventGuideExport/` → `Views/EventsExport/`
- `Views/Moderation/` → `Views/EventsModeration/`
- `Views/GuideAdmin/` → `Views/EventsAdmin/`
- `Views/CampEvents/` → `Views/BarrioEvents/`

Rename view files inside each folder where they're named after old concepts (`GuideSettings.cshtml` → `Settings.cshtml`, `GuideCategoryForm.cshtml` → `CategoryForm.cshtml`, etc. — keep the *action* name match).

**2.8 — Tests folder.**

`git mv` to canonical layout:
- `tests/Humans.Application.Tests/Services/EventGuideServiceTests.cs` → `tests/Humans.Application.Tests/Events/EventServiceTests.cs`
- `tests/Humans.Application.Tests/Repositories/EventGuideRepositoryTests.cs` → `tests/Humans.Application.Tests/Events/EventRepositoryTests.cs`
- `tests/Humans.Application.Tests/Architecture/EventGuideArchitectureTests.cs` → `tests/Humans.Application.Tests/Architecture/EventsArchitectureTests.cs` (stays in Architecture/, just rename)
- `tests/Humans.Domain.Tests/Entities/GuideEventTests.cs` → `EventTests.cs` (stays in Domain.Tests/Entities/)

**2.9 — Role + policy.**

```
reforge references RoleNames.GuideModerator
reforge references RoleGroups.GuideModeratorOrAdmin
reforge references PolicyNames.GuideModeratorOrAdmin
```

- `RoleNames.GuideModerator = "GuideModerator"` → `RoleNames.EventsAdmin = "EventsAdmin"`
- `RoleGroups.GuideModeratorOrAdmin` → `RoleGroups.EventsAdminOrAdmin`
- `PolicyNames.GuideModeratorOrAdmin` → `PolicyNames.EventsAdminOrAdmin`
- `AuthorizationPolicyExtensions.cs` policy registration + `RoleChecks.cs:65` and `RoleNames.cs:95` allowlist entry.

**Inbound-link sweep target:** any string literal `"GuideModerator"` in views, tests, AdminNavTree.cs:44. Plus AspNetRoles DB row — see Stage 4 for the data migration.

**2.10 — Feature flag + CORS policy + DI extraction.**

- `appsettings.json` + `appsettings.*.json`: `Features:EventGuide` → `Features:Events`.
- `EventGuideFeatureFilter.cs` → `EventsFeatureFilter.cs` + namespace + config key inside (`configuration.GetValue<bool>("Features:Events")`).
- `Program.cs:482` CORS policy name `GuideApi` → `EventsApi` (also update `[EnableCors("GuideApi")]` attribute on the API controller).
- Create `src/Humans.Web/Extensions/Sections/EventsSectionExtensions.cs` with `AddEventsSection(this IServiceCollection)` containing the 3 registrations.
- Delete the 3 inline `AddScoped` lines from `Program.cs:512–514`; call `builder.Services.AddEventsSection()` instead.

**Build verification:** at the end of Stage 2, full `dotnet build Humans.slnx -v quiet` + `dotnet test Humans.slnx -v quiet` must pass with the old table names still on disk (we haven't regenerated the migration yet). Most existing tests will pass; route arch tests in `EventsArchitectureTests.cs` will need their type references updated as part of 2.6.

Thread-reply bot finding 2 (admin URL).

### Stage 3 — Cross-section strip (boundary-fix protocol)

Order matters: strip the EF nav properties BEFORE regenerating the migration in Stage 4, so the regen captures the cross-section FK removal in the same file as the table renames.

**3.1 — Remove cross-section `HasOne(...)` from entity configs.**

Edit:
- `Configurations/EventConfiguration.cs` — drop `HasOne(e => e.Camp)`, `HasOne(e => e.SubmitterUser)`. Keep `HasOne(e => e.Category)` and `HasOne(e => e.GuideSharedVenue)` (both in-section).
- `Configurations/EventModerationActionConfiguration.cs` — drop `HasOne(m => m.ActorUser)`. Keep `HasOne(m => m.Event)`.
- `Configurations/EventFavouriteConfiguration.cs` — drop `HasOne(f => f.User)`. Keep `HasOne(f => f.Event)`.
- `Configurations/EventPreferenceConfiguration.cs` — drop `HasOne(p => p.User)`.
- `Configurations/EventGuideSettingsConfiguration.cs` — drop `HasOne(g => g.EventSettings)`.

**3.2 — Remove cross-section navigation properties from entities.**

- `Domain/Entities/Event.cs`: remove `public Camp? Camp { get; set; }` and `public User SubmitterUser { get; set; }`. Keep `CampId` and `SubmitterUserId` as bare FK columns.
- `Domain/Entities/EventModerationAction.cs`: remove `User ActorUser`.
- `Domain/Entities/EventFavourite.cs`: remove `User User`.
- `Domain/Entities/EventPreference.cs`: remove `User User`.
- `Domain/Entities/EventGuideSettings.cs`: remove `EventSettings EventSettings`.

Plus the reverse navs (other-section entities pointing back at ours): grep `Domain/Entities/` for any `ICollection<Event*>` or `Event*` properties — none expected since this is a new section, but verify.

**3.3 — Replace `.Include` chains with supplier service calls.**

In `EventRepository.cs`:
- Drop `.Include(e => e.Camp!).ThenInclude(c => c.Seasons)` (lines 135, 159, 166, 188).
- Drop `.Include(e => e.SubmitterUser).ThenInclude(u => u.UserEmails)` (lines 137, 160, 189).
- Drop `.Include(g => g.EventSettings)` on `GetEventGuideSettingsAsync` (line 20).
- Drop `_db.EventSettings.…` repo methods (`GetActiveEventSettingsAsync`, `GetEventSettingsByIdAsync`) — Stage 0 audit found NO supplier service for EventSettings.

**3.4 — Refactor call-sites in `EventService` and controllers to load Camp / User / EventSettings separately via supplier services.**

- `IUserService.GetUserInfoAsync(userId)` for submitter display names and emails — supplier API exists per Stage 0 audit. ✓
- `ICampService.GetCampsForYearAsync(year)` or per-call equivalent for camp + season — supplier API probably sufficient; verify in Stage 3.
- **EventSettings: NO supplier service exists.** Stage 0 confirmed this gap.

**3.5 — Handle the EventSettings gap.**

Per `feedback_no_god_class_splitting` and the section-align boundary-fix protocol: leave the cross-section read in place and document it. Concretely:

- Keep `EventRepository.GetEventGuideSettingsAsync` *without* the `.Include(g => g.EventSettings)`. Callers now get a settings row with a bare `EventSettingsId` (Guid).
- Where callers need the actual `EventSettings` content (gate-opening date, timezone, event name), the **controller / service** does a second read. Since there's no `IEventSettingsService`, the temporary shape is a documented `_db.EventSettings.FindAsync(id)` in `EventRepository` with a `// TODO: switch to IEventSettingsService once #719 ships` comment.
- Follow-up issue [#719](https://github.com/nobodies-collective/Humans/issues/719) tracks the supplier. `docs/sections/Events.md` records the pending supplier dependency.

Thread-reply bot finding 3.

### Stage 4 — DB + EF migration regen

Single fresh migration that captures: table renames, cross-section FK removal, seed data via `HasData`. After Stages 2 + 3 the C# model is in its final shape; EF will compute the right diff.

**4.1 — Repository ctor: `HumansDbContext` → `IDbContextFactory<HumansDbContext>`.**

Edit `EventRepository.cs`:
```csharp
private readonly IDbContextFactory<HumansDbContext> _factory;
public EventRepository(IDbContextFactory<HumansDbContext> factory) => _factory = factory;
```

Then every method opens its own context: `await using var ctx = await _factory.CreateDbContextAsync(ct);`. ~30 method bodies to refactor.

Update `EventsSectionExtensions.cs` to `AddSingleton<IEventRepository, EventRepository>()` (not `AddScoped`).

**4.2 — Remove the old migration.**

```
dotnet ef migrations remove --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

Removes `20260513151029_AddEventGuide.cs` + Designer + reverts the snapshot.

**4.3 — Generate the new migration.**

```
dotnet ef migrations add AddEventsSection --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

Verify the resulting `*_AddEventsSection.cs` body:
- Creates 7 tables with final names (`events`, `event_categories`, `event_guide_settings`, `event_venues`, `event_moderation_actions`, `event_favourites`, `event_preferences`).
- No cross-section FK constraints (the only FK from `events.SubmitterUserId` is a bare column; same for `events.CampId`, `event_moderation_actions.ActorUserId`, `event_favourites.UserId`, `event_preferences.UserId`, `event_guide_settings.EventSettingsId`).
- Indexes auto-renamed (`IX_events_CategoryId`, etc.).
- Seed data via `HasData` on `EventCategory` configuration.

**4.4 — NoOp sanity check.**

```
dotnet ef migrations add NoOpVerify --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

The generated file's `Up` + `Down` bodies must be empty. Then immediately:

```
dotnet ef migrations remove --project src/Humans.Infrastructure --startup-project src/Humans.Web
```

**4.5 — Role rename: no DB touch.**

Humans does **NOT** use ASP.NET Identity's `AspNetRoles`. Application roles live in `role_assignments` (custom entity, managed by `RoleAssignmentService` via the existing admin UI). Role name is a plain string column. There is no roles-lookup table.

Specific steps:
1. Rename the C# constant value: `RoleNames.GuideModerator = "GuideModerator"` → `RoleNames.EventsAdmin = "EventsAdmin"` (Stage 2.9 already covers the source rename).
2. **Do NOT touch `role_assignments`.** No `migrationBuilder.Sql`. No `HasData`. No `InsertData` / `UpdateData` / `DeleteData`. The `feedback_never_manual_db_edits` rule prohibits all of these.
3. Production: feature isn't shipped → zero `RoleName = "GuideModerator"` rows → nothing to do.
4. QA: any existing holders (likely 0–2 since the feature is brand new and feature-flag-gated) lose moderator access on deploy because `IsInRole("EventsAdmin")` won't match their `"GuideModerator"` row.
5. **Re-grant via `/Profile/{id}/Roles/Add`** (existing `RoleAssignmentService.AssignRoleAsync` path). Two clicks per holder.
6. Orphan `RoleName = "GuideModerator"` rows sit inert — no role check matches them. Optionally revoked via the same admin UI later (`EndRoleAsync`). They never grant access.

No data migration, no manual SQL, no seed manipulation.

**4.6 — Test the migration round-trip.**

- Drop a local test DB, apply migrations, verify schema.
- Run `dotnet test Humans.slnx -v quiet` — integration tests against the renamed schema.
- Smoke-test on preview: `https://539.n.burn.camp` after push deploys.

### Stage 5 — Arch tests + GDPR + interface budget

**5.1 — IUserDataContributor (closes bot finding 4).**

- Add `GdprExportSections.Events = "Events"` constant in `src/Humans.Application/Interfaces/Gdpr/GdprExportSections.cs`.
- `EventService : IEventService, IUserDataContributor`. Implement:
  - `ContributeForUserAsync(Guid userId, CT)` returning `UserDataSlice[]` for the user's `event_favourites` + `event_preferences` rows.
  - Deletion contract (whichever method `IUserDataContributor` declares for sweep-on-delete — match the existing `UserService` / `ConsentService` pattern).
- DI in `EventsSectionExtensions.cs`:
  ```csharp
  services.AddSingleton<EventService>();
  services.AddSingleton<IEventService>(sp => sp.GetRequiredService<EventService>());
  services.AddSingleton<IUserDataContributor>(sp => sp.GetRequiredService<EventService>());
  ```
- Tests: `tests/Humans.Application.Tests/Events/EventServiceTests.cs` adds contribution + deletion cases.

Thread-reply bot finding 4.

**5.2 — Section-specific arch tests to add** (in `EventsArchitectureTests.cs`).

- `OnlyEventRepository_References_EventsDbSet` (and same for each of 7 DbSets) — single-writer rule. Use Roslyn or reflection scan over `IHumansDbContext` property access.
- `EventModerationActionRepository_HasNoUpdateOrDeleteMethods` — append-only rule.
- `EventsAdminController_LivesUnderEventsAdminRoute` — `RouteFor<EventsAdminController>().Should().Be("Events/Admin")`.
- `EventService_ImplementsIUserDataContributor` — pins §8a after 5.1 lands.
- `EventsFeatureFilter_RegistersAsScoped` — pin DI lifetime.
- Update the existing route tests to reflect renamed types (already covered in Stage 2.6 fallout).

**5.3 — Interface budget.**

```
reforge members IEventService
reforge members IEventRepository
```

Final method counts after Stage 3 (some methods removed when cross-section navs stripped). Capture both:
- If `tests/Humans.Application.Tests/Architecture/InterfaceMethodBudgetTests.cs` exists, add `[typeof(IEventService)] = N` and `[typeof(IEventRepository)] = M`.
- If it doesn't exist, create it with current Camps/Teams budgets pinned as a baseline + the two new entries. (One-time addition.)

Stage 7 (simplify pass, optional) trims the interfaces and lowers the numbers.

**5.4 — Email interface trim** (closes bot finding 7).

Per Stage 0 decision #4: trim the 8 new methods to 2.

- `IEmailRenderer`: replace the 4 `RenderEvent*` methods with one `RenderEventLifecycleAsync(EventLifecycleNotification request, string? culture = null)`.
- `IEmailService`: replace the 4 `SendEvent*Async` methods with one `SendEventLifecycleNotificationAsync(EventLifecycleNotification request, string userEmail, CancellationToken ct = default)`.
- `EventLifecycleNotification` is a record DTO carrying `EventStatus newStatus`, `userName`, `eventTitle`, `reason?`, `actionUrl?`.
- Update `EmailRenderer`, `SmtpEmailService`, `StubEmailService`, `OutboxEmailService` implementations.
- Update 3 controller call sites.

Net interface bump on `IEmailService` + `IEmailRenderer`: was 8, now 2.

Thread-reply bot finding 7 with the trim + reference to #712.

### Stage 6 — `/pr-review` + closing audits + docs

Already-brief plan covers this; nothing to expand. Closing audit re-runs `/section-align Events`; expected output "clean except for `<pending supplier: Shifts/Calendar IEventSettingsService>`".

---

## Phase plan (original — superseded by Stage detail above)

(After Stop conditions resolved.)

### Phase 1 — Surface alignment (Sonnet subagents)
1. **Rename pass — depends on Stop 1 outcome.** If Option A:
   - Rename role `GuideModerator → EventsAdmin` (18 source refs + DB role row migration).
   - Rename service / repo / controllers / views / VMs / namespaces / folders per A1.1 table.
   - Run **inbound-link sweep** after every rename group (Razor `asp-controller=`, `Url.Action`, allow-lists, ViewComponents — see Phase 1 skill section).
   - Move `GuideAdminController` route from `/Admin/Guide*` to `/Events/Admin/*` (A1.3).
   - Move DI from `Program.cs` into new `Extensions/Sections/EventsSectionExtensions.cs` (A1.7).
   - Move 6 ViewModels under `Models/Events/` or rename per A1.5.
   - Rename feature flag `Features:EventGuide → Features:Events` + CORS policy `GuideApi → EventsApi`.
2. **Move tests** to `tests/Humans.Application.Tests/Events/` (A3.1); rename test files to drop `Guide` prefix.
3. **DB table rename migration** — pending Stop 2 — `RenameEventsTables` migration switching all 7 tables to `events_*` prefix.

### Phase 2 — Fix arch + bot review items + axes 2/3 (Opus where nuanced)
1. **Bot blocker fixes:**
   - 3 Razor `selected="@(bool)"` bug sites in `CampEventForm.cshtml`.
   - Display-name HARD RULE: switch to `Profile.BurnerName` via `IUserInfoService` (ModerationController:155, GuideApiController:55).
   - Public API PII leak: strip `Email` from anonymous DTO; use display name server-side.
   - Cross-section `.Include` chains: remove all `HasOne(...)` in EF configs that point to other-section entities; remove navigation properties on `GuideEvent.Camp`, `GuideEvent.SubmitterUser`, `ModerationAction.ActorUser`, `ModerationAction.GuideEvent` (this one is intra-section, keep), `UserEventFavourite.User`, `UserGuidePreference.User`, `GuideSettings.EventSettings`. Switch service-layer reads to supplier APIs (Stop 3).
   - Enum `||`-chain → `.Contains()` allowed-values pattern (`EventGuideRepository.cs:175`).
   - Implement `IUserDataContributor` on the renamed `EventsService`; add `GdprExportSections.Events`; wire DI forwarding; tests.
   - Decision on Stop 4: trim or document `IEmailService` / `IEmailRenderer` additions.
2. **Repository → IDbContextFactory:** convert `EventsRepository` to factory-based; switch DI to `AddSingleton`.
3. **Add missing arch tests:** single-writer (`Only*Repository_References_<DbSet>`), `ModerationActionRepository_HasNoUpdateOrDelete`, `EventsService_ImplementsIUserDataContributor`, route pins for renamed `EventsAdminController`.
4. **Find or create `InterfaceMethodBudgetTests`**; pin `IEventsService` / `IEventsRepository` budgets.
5. **Add missing coverage** for the section-doc invariants/negatives/triggers list (A3.2).

### Phase 3 — Simplify (Opus)
1. Trim `IEventsService` / `IEventsRepository` — collapse status-split methods to `GetAll()` + caller-side filter; move single-call-site methods private; consolidate read-shapes (target: cut both interfaces by ~40%, from ~43 to ~25 methods).
2. Replace inline user-display markup with `<vc:human>` in section views (A2.5a).
3. Extract `<vc:event-card>` ViewComponent for the 5+ event-list rendering sites.
4. Prune any redundant tests surfaced in the test bodies.
5. Optional: run Stryker.NET against the section to drive a behavior-coverage queue.

### Phase 4 — Docs (Opus)
1. Refresh `docs/sections/Events.md` post-rename — fix every name that changed, fix the false invariant claims (the doc currently says cross-section navs are only included inside this section's repo, which is true — but the doc says the navs are "retained for query convenience", a polite way of saying "they're a §6 violation we'd like to keep"; remove after Phase 2 strips them).
2. Update `docs/architecture/data-model.md` with the renamed tables.
3. Update `docs/architecture/dependency-graph.md` Events outbound edges (Camps, Users via UserInfo, Shifts/Calendar via EventSettings service).
4. Update `docs/features/26-events.md` and `27-guide-browser.md` (rename `27-events-browser.md`).
5. Section doc must list any pending supplier `/section-align` follow-ups.

### Phase 4 closing — re-run `/section-align Events` audit on the renamed branch. Expected end state: clean except for documented follow-up suppliers.

---

## Estimated effort

| Phase | Touched files (rough) | Estimated subagent passes |
|-------|------------------------|----------------------------|
| Phase 1 | 80–100 (broad renames + view link sweep) | 5–7 |
| Phase 2 | 25–35 (cross-section fixes + bot blockers + IDbContextFactory) | 6–10 |
| Phase 3 | 10–15 (interface trim, VC extraction, test prune) | 3–5 |
| Phase 4 | 5–8 (docs only) | 2 |

Total: a substantial multi-session effort. The skill recommends `/cls` between Phase 0 and Phase 1, and again if Phase 2/3 push context past 200k.

---

## Open Phase 0 closing items

Before greenlighting Phase 1, the orchestrator should also:

1. Verify coverage of section-doc invariants/negatives/triggers by reading `EventGuideServiceTests.cs`, `EventGuideRepositoryTests.cs`, `EndpointAuthorizationTests.cs`, `AuthorizationPolicyTests.cs`.
2. Confirm supplier-side APIs exist for the three cross-section consumer fixes (Stop 3).
3. Spot-check the 4 new `IEmailService` methods and the matching `OutboxEmailService` `NotSupportedException` smell.
4. Grep section views for inline `DisplayName` / `Email` user-display patterns (A2.5a closing).

These can be folded into the Phase 1 kickoff brief so the impl session lands with full context.
