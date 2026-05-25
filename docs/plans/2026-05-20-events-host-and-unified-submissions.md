# Plan — Event Host field + unified My Event Submissions

**Branch:** `feat/events-host-and-unified-submissions`
**Guide:** `docs/features/26-events.md` (updated in this branch)

## Goals

1. **Host field** — optional free-text (`≤ 40 chars`) on every event naming who runs it.
   - Individual events: published guide attributes the event to `Host` when set, else the submitter's name.
   - Barrio events: `Host` is supplementary detail; blank = no host line, event stays attributed to the barrio.
2. **Unified submissions page** — `/Events/MySubmissions` shows the human's own individual events in one block plus one block per barrio they lead. Retire the per-barrio page `/Barrios/{slug}/Events`.

Scope decisions (confirmed): **relocate only** — no barrio-lead pre-approval step (the mockup's "Approve" column is illustrative); barrio events still flow straight to the global moderation queue. The old `/Barrios/{slug}/Events` route is **removed entirely** (no redirect).

---

## Part A — Host field

### A1. Domain — `src/Humans.Domain/Entities/Event.cs`
- Add `public string? Host { get; set; }` with XML doc ("Optional free-text name of who runs the event, ≤ 40 chars").

### A2. Infrastructure — EF config + migration
- Add `Host` column config (max length 40, nullable) in the `Event` entity configuration (`src/Humans.Infrastructure/.../EventConfiguration.cs` or the fluent block in `HumansDbContext`).
- Generate one migration: `dotnet ef migrations add AddEventHost -p src/Humans.Infrastructure -s src/Humans.Web`. Review per EF migration discipline (`memory/`), `-v quiet`.

### A3. View models — `Models/Events/IndividualEventViewModels.cs`, `BarrioEventViewModels.cs`
- `IndividualEventFormViewModel`: add `[MaxLength(40)] [Display(Name = "Host")] public string? Host { get; set; }`.
- `CampEventFormViewModel`: same `Host` property.
- Row VMs (`IndividualEventRowViewModel`, `CampEventRowViewModel`): no host column needed on the management tables unless we want it shown — keep tables as-is.

### A4. Forms — set/read Host
- `IndividualEventForm.cshtml` + the barrio form: add a Host input next to Location Note (same `mb-3` input pattern, `maxlength="40"`, placeholder e.g. "e.g. The Tea Collective").
- `EventsController.Create/Update` and the barrio submit/update actions: copy `model.Host` onto the entity; populate `model.Host` from the entity on Edit.

### A5. Attribution — Browse, Schedule, public API
- `EventsController.Browse`: `BrowseEventItem.SubmitterName` currently set only for individual events. New rule:
  - individual event display host = `e.Host ?? submitterName`
  - barrio event display host = `e.Host` (when present)
  - Add a `Host`/display field to `BrowseEventItem` and surface it in `Browse.cshtml`.
- `Schedule` / `ScheduleItemViewModel`: add host display following the same fallback (optional; lower priority).
- Public API `GuideEventApiDto` (`EventsApiModels.cs`): add `string? Host`. In the API projection, resolve individual-event host fallback to submitter name so the PWA shows attribution. Update `EventsApiController` mapping + any cached `ApprovedEventView` projection to carry `Host` (and submitter name where needed). **Note the §15 cache:** `ApprovedEventView` is pre-stitched at warm time — add `Host` there.

---

## Part B — Unified My Event Submissions

### B1. `ICampService` — list barrios a user manages events for
- Add `Task<IReadOnlyList<CampLookup>> GetEventManagedCampsAsync(Guid userId, int year, ...)` (or similar) returning camps where the user passes the existing `IsUserCampEventManagerAsync` check for the active season. Implement in `CampService` reusing the Lead/Workshop/CampAdmin OR-logic (issue #753). This replaces per-slug resolution for building the page.

### B2. `MySubmissionsViewModel` rebuild — `Models/Events/IndividualEventViewModels.cs`
- Restructure into:
  - `PersonalBlock` — existing counts + `List<IndividualEventRowViewModel>` (individual events).
  - `List<BarrioBlockViewModel> Barrios` — each with `CampName`, `CampSlug`, counts, `List<CampEventRowViewModel>`, `CanSubmit` (submission open).
  - Shared submission-window fields (`IsSubmissionOpen`, open/close, tz).

### B3. `EventsController.MySubmissions` — load both
- Load `guide.GetUserSubmissionsAsync(user.Id)` for the personal block (unchanged).
- Load event-managed camps via B1; for each call `guide.GetCampSubmissionsAsync(camp.Id)` and build a barrio block. (At ~500 users / few camps per lead this N+1 is fine per scale guidance.)

### B4. Barrio submit/edit/withdraw move into `EventsController`
- Add actions under the `Events` route, e.g.:
  - `GET  /Events/Barrio/{slug}/Submit` → barrio form
  - `POST /Events/Barrio/{slug}/Submit`
  - `GET  /Events/Barrio/{slug}/{eventId}/Edit`, `POST` update
  - `POST /Events/Barrio/{slug}/{eventId}/Withdraw`
- Port the body from `BarrioEventsController` (camp resolution via `ResolveCampEventManagementAsync`, submission-window guard, email, logging). All redirect back to `MySubmissions` (no per-barrio Index).
- Authority: keep `IsUserCampEventManagerAsync` gate. `EventsController` derives from `HumansControllerBase`; the camp-resolution helper lives on `HumansCampControllerBase`. **Decide:** either move the resolve helper to a shared location or inject `ICampService` + `IAuthorizationService` into `EventsController` and inline the check. Prefer extracting the helper so the Lead/Workshop authorization stays single-sourced.

### B5. Views
- Rewrite `Views/Events/MySubmissions.cshtml`: a "My personal events" card (counts + table + "Submit new event"), then a card per barrio (counts + table + "Add barrio event"), matching the mockup but in Humans' Bootstrap card/table idiom already used by the current two pages.
- Reuse the barrio form view (move `Views/BarrioEvents/BarrioEventForm.cshtml` → `Views/Events/BarrioEventForm.cshtml`, retarget its `asp-action`/route to the new `/Events/Barrio/...` actions and breadcrumb back to MySubmissions).
- Delete `Views/BarrioEvents/Index.cshtml`.

### B6. Remove the old route
- Delete `Controllers/BarrioEventsController.cs` and the `Views/BarrioEvents/` folder.
- `Views/Camp/Details.cshtml` line ~400: change "Manage Events" link from `asp-controller="BarrioEvents" asp-action="Index" asp-route-slug` to `asp-controller="Events" asp-action="MySubmissions"` (no slug). Label stays "Manage Events".
- Check `CampOperationRequirement.cs` / `HumansCampControllerBase.cs` for `BarrioEventsController` coupling and clean up.

---

## Part C — Docs, tests, arch

### C1. Architecture test — `tests/.../Architecture/EventsArchitectureTests.cs`
- `EventsRoutes_UseEventsAndBarriosSlugs` and `EventsRoutes_DoNotExposeOldEventGuideOrCampsSlugs` reference `BarrioEventsController` (lines 89, 102). Remove those references; the controller no longer exists. Add an assertion if desired that barrio event management lives under the `Events` route.
- Update `Baselines/NoBusinessLogicInControllers.baseline.txt` (drops `BarrioEventsController` entries; may add `EventsController` entries — regenerate per the baseline's process).

### C2. Section invariant doc — `docs/sections/Events.md`
- Routing table: drop the `BarrioEventsController` / `/Barrios/{slug}/Events` row; note barrio event management now lives on `EventsController` under `/Events/Barrio/{slug}/*`.
- Data model: add `Host` (string?, max 40) to the GuideEvent table.
- Actors & Negative rules: update wording that referenced `BarrioEventsController` / `CampEventsController`.

### C3. Tests
- Controller/integration tests referencing `/Barrios/{slug}/Events` → new `/Events/Barrio/{slug}/*` routes.
- Add coverage: Host persists on submit/edit (both flows); browse/API attribution fallback (host vs submitter name); MySubmissions renders personal + barrio blocks for a multi-barrio lead and personal-only for a non-lead.

---

## Suggested commit slices
1. Domain + EF migration for `Host` (A1–A2).
2. Host on forms/controllers + attribution (A3–A5).
3. Unified MySubmissions: VM + controller + views + camp link, remove BarrioEvents (B1–B6).
4. Arch test + baseline + section doc + tests (C1–C3).

## Open risks
- §15 approved-events cache: `Host` must be added to the `ApprovedEventView` projection and warmup, or the API/Browse won't show it until restart.
- Extracting camp-resolution authority out of `HumansCampControllerBase` without duplicating the #753 Lead/Workshop logic — single-source it.
