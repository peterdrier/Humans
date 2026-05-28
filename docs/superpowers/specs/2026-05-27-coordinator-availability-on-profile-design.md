# Coordinator Availability on the Profile

**Date:** 2026-05-27
**Branch:** `feature/coordinator-availability-on-profile`
**Section:** Profiles (host) → Shifts → Volunteer Tracking (data + writes)

## Overview

When a Volunteer Coordinator or Admin views **another human's** profile (`/Profile/{id}`), surface that volunteer's **barrio-build window** at a glance and let the coordinator adjust it in place:

- **which build days the volunteer declared as participating** (`GeneralAvailability.AvailableDayOffsets`),
- **when they leave for barrio set-up** (`VolunteerBuildStatus.BarrioSetupStartDate`),
- **which days they've taken off** (`VolunteerBuildStatus.DayOffs`),

alongside the build-day signups/gaps the heatmap already computes.

Today this information lives **only** on the Volunteer Tracking heatmap (`/Shifts/Dashboard/VolunteerTracking`), as a whole-cohort grid. A coordinator looking at one person's profile sees no-show history and a shift-signups summary, but none of the build-window picture, and has no way to update a volunteer's declared days after a conversation ("I can actually come the 28th too").

This change embeds the **existing** Volunteer Tracking heatmap strip — scoped to one volunteer — as a coordinator-only card on the profile, and adds a per-day **"mark / unmark available"** toggle to the cell popover. The camp-setup and day-off controls already exist in that popover; they simply become reachable from the profile too.

**Scope boundary:** build window only (day offsets `BuildStartOffset … -1`, i.e. before gate-opening) — the same window the heatmap already renders, which is exactly the "barrio build" lens. Event-day (positive-offset) availability is out of scope.

## Actors & Authorization

| Capability | Gate | Reuses |
|---|---|---|
| **See** the build strip on another human's profile | `viewerIsCoordinator \|\| ShiftRoleChecks.IsPrivilegedSignupApprover(User)` | Identical to the existing no-show-history / shift-signups gate in `ProfileController.BuildNoShowHistoryContextAsync` (`ProfileController.cs:1887`). |
| **Edit** (availability toggle, camp set-up, day off) | Policy `VolunteerTrackingWrite` (Admin + VolunteerCoordinator) | The partial self-resolves this gate today (`_VolunteerHeatmap.cshtml:9`); each write action also carries `[Authorize(Policy = VolunteerTrackingWrite)]`. |

Negative rules:
- The strip is **never shown on the human's own profile** (`isOwnProfile` short-circuits, same as no-show history).
- A non-coordinator, non-approver viewer sees the profile exactly as today — no strip, no leaked data.
- A coordinator who can view but lacks `VolunteerTrackingWrite` sees the strip read-only (no edit forms render — existing `canWrite` gate in the partial).

## The single-volunteer strip

A new card titled **"Build availability & tracking"**, grouped with the existing coordinator-only shift sections in `Views/Profile/Index.cshtml`. It renders the **existing** `Views/VolunteerTracking/_VolunteerHeatmap.cshtml` partial with a one-row model for this volunteer.

### Day range & cell states

Columns span `BuildStartOffset … -1` (build window), dates derived from `GateOpeningDate`, identical to the cohort heatmap. Cell state precedence is unchanged from `VolunteerTrackingService.GetTrackingDataAsync` (camp-setup > outside-window > signup(Confirmed/Pending) > day-off > gap). The single-user computation always renders the volunteer regardless of whether they'd land in the cohort heatmap's "main" or "unbooked" bucket.

### New per-cell datum: `DeclaredAvailable`

Each cell gains a `bool DeclaredAvailable` (is this day offset in the volunteer's `GeneralAvailability.AvailableDayOffsets`?). This is **orthogonal** to `State` — a Confirmed day can also be declared-available — so it cannot be folded into the existing mutually-exclusive `VolunteerCellState` enum. The popover uses it to render the toggle as "Mark available" vs "Unmark available". The cohort heatmaps ignore the field.

## Edit interactions (popover)

All edits happen in the existing cell popover and POST to **`VolunteerTrackingController`** (Approach 1 — writes stay consolidated in one controller; the profile is a pure host). Each form carries a hidden `returnUrl` so the coordinator lands back on the profile after the redirect.

| Control | When shown | Action | Status |
|---|---|---|---|
| Mark / Move / Clear camp set-up | per existing state logic | `SetCampSetup` / `ClearCampSetup` | **exists** |
| Mark / Cancel day off | per existing state logic (Gap → mark; DayOff → cancel) | `SetDayOff` / `ClearDayOff` | **exists** |
| **Mark / Unmark available** | when `ShowAvailabilityControls` (profile only) | `SetAvailabilityDay` / `ClearAvailabilityDay` | **new** |

The availability toggle is gated by a new `HeatmapPartialModel.ShowAvailabilityControls` flag, defaulted `false`. The profile passes `true`; the Volunteer Tracking page passes `false`, so **its behavior is unchanged**. (Deliberate: keeps this change scoped to the profile and avoids silently altering the tracking page's main heatmap.)

### `returnUrl` mechanism

The partial emits `returnUrl = Context.Request.Path + Context.Request.QueryString` into each form. Every write action (4 existing + 2 new) gains an optional `string? returnUrl` parameter and redirects via:

```csharp
return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
```

On the tracking page this resolves to the tracking page path (equivalent to today's `RedirectToAction(nameof(Index))`, now also preserving its filter query string — a harmless improvement). On the profile it returns to `/Profile/{id}`. `Url.IsLocalUrl` blocks open-redirects.

## Architecture

Clean Architecture layers, respecting the constitution (`docs/architecture/peters-hard-rules.md`): cross-section calls go through `I<Section>ServiceRead` at the service layer; controllers hold no logic beyond parse/call/format; read-modify-write lives in services.

### Domain

- `Humans.Domain/Enums/AuditAction.cs`: add `VolunteerAvailabilitySet`, `VolunteerAvailabilityCleared`.

### Application

- **New cross-section read interface** `IVolunteerTrackingServiceRead` (`Interfaces/Shifts/`), implemented by the existing `VolunteerTrackingService`. Mirrors `ITeamServiceRead`. `[SurfaceBudget(1)]`.
  ```csharp
  public interface IVolunteerTrackingServiceRead
  {
      /// One volunteer's build-window strip, or null when there is no active event.
      Task<VolunteerBuildStripDto?> GetUserBuildStripAsync(Guid userId, CancellationToken ct = default);
  }
  ```
  `IVolunteerTrackingService : IVolunteerTrackingServiceRead` — the full interface keeps the cohort read + the write methods.

- **New DTO** `VolunteerBuildStripDto` (`DTOs/`):
  ```csharp
  public sealed record VolunteerBuildStripDto(
      int BuildStartOffset,
      LocalDate GateOpeningDate,
      VolunteerHeatmapRow Row);   // reuses the existing row DTO
  ```

- **`VolunteerCell`** (`DTOs/VolunteerTrackingViewModel.cs`): add `bool DeclaredAvailable` (last positional field; cohort call sites set it from the availability set already loaded in `GetTrackingDataAsync`).

- **`VolunteerTrackingService`**:
  - Implement `GetUserBuildStripAsync`. Refactor the per-day cell-building loop out of `GetTrackingDataAsync` into a private helper `BuildCells(...)` so the cohort path and the single-user path share one computation (DRY). The helper also sets `DeclaredAvailable` from the user's availability set.
  - No new repository method: it already injects `IGeneralAvailabilityRepository`, `IVolunteerTrackingRepository`, `IShiftManagementRepository` — the single-user read filters the same loads in memory (~500 users; per project scale guidance, no targeted query needed).

- **`IGeneralAvailabilityService`**: add
  ```csharp
  /// Add (available=true) or remove (available=false) one build-day offset
  /// from the user's declared availability. Read-modify-write; preserves
  /// any out-of-build offsets; invalidates the user's shift view cache.
  Task SetDayAvailabilityAsync(
      Guid userId, Guid eventSettingsId, int dayOffset, bool available,
      CancellationToken ct = default);
  ```
  Implementation in `GeneralAvailabilityService`: load current offsets via `GetByUserAndEventAsync` (null row → empty set, so `available=true` upserts a fresh row), add/remove `dayOffset`, `UpsertAsync`, `_viewInvalidator.InvalidateUser(userId)`. The whole-list `SetAvailabilityAsync` (volunteer self-service) is untouched. (Per-day logic goes in the service, not the controller — constitution.)
  - **Range guard:** reject `dayOffset` outside the build window (`dayOffset < BuildStartOffset || dayOffset >= 0`) — mirrors `SetDayOffAsync`'s guard. The toggle only renders on in-build cells, so this only defends against hand-crafted POSTs; return a no-op/false result rather than throwing.

### Web

- **`VolunteerTrackingController`** (Shifts section — already the home of these writes):
  - Two new actions, mirroring the existing camp-setup/day-off pattern (antiforgery, `VolunteerTrackingWrite`, audit, redirect-with-`returnUrl`):
    ```csharp
    [HttpPost("SetAvailabilityDay")]   public Task<IActionResult> SetAvailabilityDay(Guid userId, int dayOffset, string? returnUrl, CancellationToken ct);
    [HttpPost("ClearAvailabilityDay")] public Task<IActionResult> ClearAvailabilityDay(Guid userId, int dayOffset, string? returnUrl, CancellationToken ct);
    ```
    Each resolves the active event id via the injected `IShiftManagementService.GetActiveAsync()` (same precedent as `ShiftsController.SaveAvailability`), calls `IGeneralAvailabilityService.SetDayAvailabilityAsync`, emits the audit entry, redirects.
  - Add optional `string? returnUrl` to the 4 existing write actions; swap their tail `RedirectToAction(nameof(Index))` for the `Url.IsLocalUrl` redirect shown above.
  - New ctor deps: `IGeneralAvailabilityService`, `IShiftManagementService`.

- **`_VolunteerHeatmap.cshtml`**: forms gain `asp-controller="VolunteerTracking"` (so they route correctly when hosted off the profile) + a hidden `returnUrl`. Add the availability toggle block, gated by `Model.ShowAvailabilityControls` and `canWrite`, driven by `cell.DeclaredAvailable`. No change to the camp-setup/day-off blocks beyond the controller/returnUrl attributes.

- **`HeatmapPartialModel`** (`Models/VolunteerHeatmapPartialModels.cs`): add `bool ShowAvailabilityControls` (default false).

- **New `VolunteerBuildStripViewComponent`** (`Web/ViewComponents/`) — the host for the strip on the profile, mirroring the **existing** `ShiftSignupsViewComponent` (which is exactly the analogous coordinator-only shift section on the profile, and itself cross-section-calls `ITeamServiceRead`). Injects `IVolunteerTrackingServiceRead`; `InvokeAsync(Guid userId)` calls `GetUserBuildStripAsync(userId)`, maps the result to `HeatmapPartialModel([Row], BuildStartOffset, GateOpeningDate, displayNameByUserId, ShowAvailabilityControls: true)`, and returns the **existing** `_VolunteerHeatmap.cshtml` partial as its view (`return View("~/Views/VolunteerTracking/_VolunteerHeatmap.cshtml", model)`). Returns empty content when the strip is null (no active event). The `displayNameByUserId` is the single `{ userId → BurnerName }` entry (looked up via `IUserService`, as the tracking page does).

  This keeps **`ProfileController` and `ProfileViewModel` unchanged** — the strip is purely a view-layer addition, like `<vc:shift-signups>`.

- **`Views/Profile/Index.cshtml`**: under the existing coordinator gate, immediately after `<vc:shift-signups ... />`, add `<vc:volunteer-build-strip user-id="@Model.UserId" />`. The existing `@if (!Model.IsOwnProfile && Model.CanViewShiftSignups)` block already enforces the view gate (the spec's required `coordinator || privileged-approver` rule) — reused, no new flag.

- **Localization**: add `VolTrack_Popover_MarkAvailable` / `VolTrack_Popover_UnmarkAvailable` (+ a card title key, e.g. `Profile_BuildStrip_Title`) to every locale `.resx`, mirroring the existing `VolTrack_Popover_*` keys.

### DI

`ShiftsSectionExtensions.cs`: register `IVolunteerTrackingServiceRead` against the **same instance** as `IVolunteerTrackingService`. VolunteerTracking has no caching decorator, so follow the local `GeneralAvailabilityService` precedent (lines 53–55) rather than the Teams decorator pattern — register the concrete type once and map both interfaces to it:

```csharp
services.AddScoped<ShiftsVolunteerTrackingService>();
services.AddScoped<IVolunteerTrackingService>(sp => sp.GetRequiredService<ShiftsVolunteerTrackingService>());
services.AddScoped<IVolunteerTrackingServiceRead>(sp => sp.GetRequiredService<ShiftsVolunteerTrackingService>());
```

(Replaces the current direct `AddScoped<IVolunteerTrackingService, ShiftsVolunteerTrackingService>()` at line 59.) No new service classes.

## New durable surface (Reuse-First audit)

Per `CLAUDE.md` → Reuse-First Change Discipline. Interface/public surface requires Peter's approval (this spec review is that gate).

| New surface | Necessary because | Reuse rejected |
|---|---|---|
| `IVolunteerTrackingServiceRead` + `GetUserBuildStripAsync` | Constitution mandates cross-section reads via a `*Read` interface; Profile needs one volunteer's build strip. | `GetTrackingDataAsync` computes the whole cohort, buckets each person into main *xor* unbooked, and lacks the unified per-day `DeclaredAvailable`. Filtering its output can't produce the profile row. |
| `VolunteerBuildStripDto` | Carries the single row + window metadata the partial needs. | Reuses `VolunteerHeatmapRow` inside it; only the wrapper is new. |
| `VolunteerCell.DeclaredAvailable` (field) | Availability is orthogonal to the mutually-exclusive `State` enum. | A parallel single-user cell DTO would duplicate `VolunteerCell`; one additive field is smaller. |
| `IGeneralAvailabilityService.SetDayAvailabilityAsync` | Per-day add/remove is logic that the constitution forbids in the controller. | `SetAvailabilityAsync` replaces the entire list; calling get+set from the controller would put read-modify-write logic in the controller. |
| `HeatmapPartialModel.ShowAvailabilityControls` (field) | Scope the new toggle to the profile; leave the tracking page unchanged. | No existing flag distinguishes hosts. |
| `VolunteerBuildStripViewComponent` (Web class) | Host the strip on the profile without touching `ProfileController`/`ProfileViewModel`. | Directly mirrors the existing `ShiftSignupsViewComponent` (same role, same `*Read` cross-section pattern). Renders the existing `_VolunteerHeatmap` partial — no new view file. |
| 2 controller actions + 2 audit enum values (`VolunteerAvailabilitySet`/`Cleared`, appended at the end of the positional enum) | New capability; no existing endpoint edits a volunteer's availability on their behalf. | — |
| `returnUrl` params on 4 existing actions | Lets the shared forms return to the profile. | Optional + `Url.IsLocalUrl`-validated; default path preserves current behavior. |

No new repository, no new service class, no schema/migration, no new dependency.

## Testing

TDD on the regression-prone service logic; lighter coverage on wiring.

### Service tests (`Humans.Application` test project, Shifts)
- `SetDayAvailabilityAsync`: add a new offset; remove an existing offset; remove a non-present offset (no-op); preserves out-of-build offsets; calls `InvalidateUser`.
- `GetUserBuildStripAsync`: null when no active event; row cells carry correct `State` and `DeclaredAvailable`; volunteer with no signups but with declared days; volunteer with **no `GeneralAvailability` row at all** → all cells `DeclaredAvailable = false`, row still returned; volunteer with signups; camp-setup and day-off reflected in cells; window metadata correct.
- `SetDayAvailabilityAsync` out-of-build `dayOffset` → no-op/false, no upsert (range guard).
- Regression: `GetTrackingDataAsync` cohort output unchanged after the `BuildCells` refactor (existing tests should stay green; add an explicit `DeclaredAvailable` assertion on a cohort row).

### Controller tests (`Humans.Web` tests, `VolunteerTrackingControllerTests`)
- `SetAvailabilityDay` / `ClearAvailabilityDay`: anonymous → challenge; authenticated without `VolunteerTrackingWrite` → 403; with policy → calls service + emits `VolunteerAvailabilitySet` / `VolunteerAvailabilityCleared` audit + redirects.
- `returnUrl` honored only when `Url.IsLocalUrl` (local path → `LocalRedirect`; off-site/absolute → falls back to `Index`).
- Existing camp-setup/day-off actions still redirect to `Index` when `returnUrl` is null (no behavioral regression).

### View gate (Index.cshtml)
- The strip renders only inside the existing `!IsOwnProfile && CanViewShiftSignups` block (same gate as `<vc:shift-signups>`). No own-profile / non-coordinator path renders it. (Covered by the existing `CanViewShiftSignups` computation + tests; the new `<vc:>` tag sits inside that proven gate.)

### Cross-section compliance
- By construction: the strip's only cross-section read is `IVolunteerTrackingServiceRead` (full interface never injected outside Shifts), mirroring `ShiftSignupsViewComponent`'s use of `ITeamServiceRead`. No new bespoke architecture test — per the constitution, call-site boundary rules are analyzer territory, not tests; the existing `ProfileArchitectureTests` / `ServiceBoundaryArchitectureTests` must stay green.

## Out of scope / assumptions

- **Event-day availability** (positive offsets). Build window only.
- **Bulk availability edit** on the profile — the volunteer's own whole-list `/Shifts/Mine/Availability` form stays the only multi-day editor; coordinators toggle one day at a time (matches the per-cell interaction model the user chose).
- **Rewriting `_VolunteerHeatmap` itself as a ViewComponent.** The new `VolunteerBuildStripViewComponent` *renders* the existing partial by path; the partial stays a partial (still used directly by the tracking page). Converting the partial's internals into a component is a possible later cleanup, not done here.
- **Coordinator-attribution column on `GeneralAvailability`.** No schema change; "who set it" is captured in the audit log (matches how camp-setup/day-off attribution is also audited, though those additionally store `SetByUserId` on their own entity — availability does not, and the audit trail is sufficient here).
- The pre-existing Profile→Shifts dependencies on the full `IShiftSignupService` / `IShiftManagementService` interfaces are existing tech debt (constitution §Tech debt); this change neither relies on nor extends them, and adds its new cross-section read via the compliant `*Read` interface.

## Change enforcement

- **If you add a `VolunteerCellState` value** → check the `CellClass`/`CellAria` switches in `_VolunteerHeatmap.cshtml` (and `_VolunteerUnbookedHeatmap.cshtml`) cover it; unrelated to `DeclaredAvailable` which is a separate field.
- **If you add a write action to `VolunteerTrackingController`** → give it the same `returnUrl` redirect treatment so it works when hosted off the profile.
- **If you add a new locale** → add the `VolTrack_Popover_{Mark,Unmark}Available` + card-title keys (per the project's i18n change-enforcement rule).

## Open questions

None blocking.
