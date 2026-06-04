# Per-Day Shift Signup Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the period/range signup picker on `/Shifts` with instant per-day click-to-toggle (sign up / remove) on both all-day Build/Strike rows and timed Event-shift rows, updating each row in place with no full-page reload.

**Architecture:** A single `POST /Shifts/ToggleDay` action resolves sign-up-vs-bail by `shiftId` (reusing existing `IShiftSignupService` methods), then returns the re-rendered row partial as the response body with metadata in response headers (`X-Signed-Up`, `X-Toast-*`, `X-My-Signup-Count`, or `X-Redirect`). The two table partials gain an `Interaction` mode so the **OnboardingWidget** wizard keeps its existing form-POST behavior unchanged while the browse page renders toggle buttons. One delegated JS handler drives the toggles.

**Tech Stack:** ASP.NET Core MVC (Razor partials), .NET / Clean Architecture, NodaTime, NSubstitute + xUnit, vanilla JS (`fetch` + FormData + antiforgery, the house pattern from `Views/Notifications/Index.cshtml`).

**Spec:** `docs/superpowers/specs/2026-06-04-per-day-shift-signup-design.md` — read it first.

**Constitution:** `docs/architecture/peters-hard-rules.md` — controllers parse/call/format only; no new service/interface methods (this plan adds none); no new dead surface (dead `ShiftsController` actions are deleted, caller-checked).

---

## File Structure

**New:**
- `src/Humans.Web/Views/Shared/_BuildStrikeRotaRow.cshtml` — one all-day day-row (mode-aware action cell).
- `src/Humans.Web/Views/Shared/_EventRotaRow.cshtml` — one timed-shift row (mode-aware action cell).
- `src/Humans.Web/wwwroot/js/shifts.js` — delegated `.js-day-toggle` handler.
- `tests/Humans.Web.Tests/Controllers/ShiftsControllerToggleDayTests.cs` — ToggleDay behavior.

**Modified:**
- `src/Humans.Web/Models/ShiftViewModels.cs` — add `ShiftSignupInteraction` enum, `Interaction` prop on the two table VMs, and the two row VMs.
- `src/Humans.Web/Models/Shifts/ShiftBrowsePageBuilder.cs` — add `BuildRowAsync(shiftId, user, ct)` reusing existing per-shift item construction.
- `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml` — branch on `Interaction`; FormPost unchanged, InstantToggle = flat per-day list of row partials.
- `src/Humans.Web/Views/Shared/_EventRotaTable.cshtml` — branch on `Interaction`; render row partial.
- `src/Humans.Web/Views/Shifts/Index.cshtml` — set `Interaction = InstantToggle` on the 4 table-VM construction sites; reference `shifts.js`.
- `src/Humans.Web/Controllers/ShiftsController.cs` — add `ToggleDay`; extract dietary predicate; delete dead `SignUp`/`SignUpRange`/`RedirectIfDietaryMissing*` (caller-checked).
- `src/Humans.Web/Views/Shifts/Mine.cshtml` — drop `data-confirm` on Bail/BailRange.
- Resource `.resx` (the `SharedResource` set) — new `Shifts_*` keys for button labels/toasts, all locales.

**Untouched (verify, do not change):** `Views/OnboardingWidget/Shifts.cshtml`, `OnboardingWidgetController`, `ProfileController` dietary replay, `IShiftSignupService.SignUpRangeAsync`.

---

## Chunk 1: View-model scaffolding

Compile-only additions; no behavior change. After this chunk the solution builds and all existing tests pass.

### Task 1: Interaction enum + table-VM property

**Files:**
- Modify: `src/Humans.Web/Models/ShiftViewModels.cs`

- [ ] **Step 1: Add the enum and properties**

In `ShiftViewModels.cs`, add near the other shift view enums:

```csharp
/// <summary>
/// How a shift table renders its signup affordance.
/// FormPost = legacy range form + per-shift POST (OnboardingWidget wizard).
/// InstantToggle = per-day AJAX toggle button (the /Shifts browse page).
/// </summary>
public enum ShiftSignupInteraction
{
    FormPost,
    InstantToggle
}
```

Add to **both** `BuildStrikeRotaTableViewModel` and `EventRotaTableViewModel`:

```csharp
/// <summary>Signup affordance mode. Defaults to FormPost so OnboardingWidget renders unchanged.</summary>
public ShiftSignupInteraction Interaction { get; set; } = ShiftSignupInteraction.FormPost;
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds (0 errors).

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/ShiftViewModels.cs
git commit -m "feat(shifts): add ShiftSignupInteraction mode to rota table view models"
```

### Task 2: Row view models

The row partials need a focused model. Build/Strike rows are read-only in FormPost (action cell only differs by mode), so they carry just display + mode. Event rows in FormPost need the controller/action + filter route values for the existing per-row form.

**Files:**
- Modify: `src/Humans.Web/Models/ShiftViewModels.cs`

- [ ] **Step 1: Add the row VMs**

```csharp
/// <summary>One Build/Strike all-day day-row. Action cell varies by Interaction.</summary>
public class BuildStrikeRotaRowViewModel
{
    public ShiftDisplayItem Item { get; set; } = null!;
    public EventSettings Es { get; set; } = null!;
    public bool IsSignedUp { get; set; }
    public bool SignupsBlockedByMissingDietary { get; set; }
    public ShiftSignupInteraction Interaction { get; set; } = ShiftSignupInteraction.FormPost;
}

/// <summary>One timed Event-shift row. Action cell varies by Interaction; FormPost reuses the per-row signup form.</summary>
public class EventRotaRowViewModel
{
    public ShiftDisplayItem Item { get; set; } = null!;
    public EventSettings Es { get; set; } = null!;
    public bool IsSignedUp { get; set; }
    public SignupStatus? SignupStatus { get; set; }
    public bool SignupsBlockedByMissingDietary { get; set; }
    public ShiftSignupInteraction Interaction { get; set; } = ShiftSignupInteraction.FormPost;

    // FormPost-only: where the per-row signup form posts, plus filter context for the PRG redirect.
    public string SignUpController { get; set; } = "Shifts";
    public string SignUpAction { get; set; } = "SignUp";
    public Guid? FilterDepartmentId { get; set; }
    public string? FilterFromDate { get; set; }
    public string? FilterToDate { get; set; }
    public string? FilterPeriod { get; set; }
    public IReadOnlyList<string> FilterPeriods { get; set; } = [];
    public IReadOnlyList<Guid> FilterTagIds { get; set; } = [];
    public string? Sort { get; set; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/ShiftViewModels.cs
git commit -m "feat(shifts): add per-row view models for rota row partials"
```

---

## Chunk 2: Extract row partials, preserve FormPost behavior

Pure refactor: extract each table's `<tr>` into a row partial and have the tables render it. **No behavior change in either mode yet** (InstantToggle still renders the FormPost layout until Chunk 4 — guard with a `TODO(InstantToggle)` so the build is green and onboarding + browse look identical). Verified by running the app: onboarding wizard shift step and `/Shifts` browse render exactly as before.

### Task 3: `_EventRotaRow.cshtml` partial + wire `_EventRotaTable`

**Files:**
- Create: `src/Humans.Web/Views/Shared/_EventRotaRow.cshtml`
- Modify: `src/Humans.Web/Views/Shared/_EventRotaTable.cshtml`

- [ ] **Step 1: Create `_EventRotaRow.cshtml`** — move the per-shift `<tr>` (and its description `<tr>`) out of `_EventRotaTable.cshtml` verbatim, retargeted onto `EventRotaRowViewModel`. The action cell keeps the existing FormPost branch (signup form / status badge / Full). Add a placeholder for the toggle branch:

```razor
@model Humans.Web.Models.EventRotaRowViewModel
@* … date/phase/time/duration/fill/avatars cells copied from _EventRotaTable … *@
<td>
    @if (Model.Interaction == Humans.Web.Models.ShiftSignupInteraction.InstantToggle)
    {
        @* TODO(InstantToggle): toggle button — implemented in Chunk 4. Render FormPost for now. *@
    }
    @* existing FormPost markup: SignedUp badge / Full / signup <form> posting to @Model.SignUpController/@Model.SignUpAction *@
</td>
```

- [ ] **Step 2: Wire the table** — in `_EventRotaTable.cshtml`, replace the inline `<tr>` loop body with:

```razor
@foreach (var item in Model.Shifts)
{
    @await Html.PartialAsync("_EventRotaRow", new EventRotaRowViewModel
    {
        Item = item,
        Es = es,
        IsSignedUp = Model.UserSignupShiftIds.Contains(item.Shift.Id),
        SignupStatus = Model.UserSignupStatuses.GetValueOrDefault(item.Shift.Id),
        SignupsBlockedByMissingDietary = Model.SignupsBlockedByMissingDietary,
        Interaction = Model.Interaction,
        SignUpController = Model.SignUpController,
        SignUpAction = Model.SignUpAction,
        FilterDepartmentId = Model.FilterDepartmentId,
        FilterFromDate = Model.FilterFromDate,
        FilterToDate = Model.FilterToDate,
        FilterPeriod = Model.FilterPeriod,
        FilterPeriods = Model.FilterPeriods,
        FilterTagIds = Model.FilterTagIds,
        Sort = Model.Sort
    })
}
```

Keep the `<thead>` and `<table>` wrapper in `_EventRotaTable`.

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds.

- [ ] **Step 4: Run the app and eyeball both surfaces**

Run: `dotnet run --project src/Humans.Web` (dev login). Visit `/Shifts` (Event rotas) and the onboarding shift step.
Expected: Event-shift tables render identically to before (signup buttons, badges, Full state). No layout regression.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/Shared/_EventRotaRow.cshtml src/Humans.Web/Views/Shared/_EventRotaTable.cshtml
git commit -m "refactor(shifts): extract _EventRotaRow partial (no behavior change)"
```

### Task 4: `_BuildStrikeRotaRow.cshtml` partial + wire `_BuildStrikeRotaTable`

**Files:**
- Create: `src/Humans.Web/Views/Shared/_BuildStrikeRotaRow.cshtml`
- Modify: `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml`

> **Execution refinement (2026-06-04):** the Build/Strike table renders per-day rows in two variants (collapsed-range detail rows with `ps-4`/`d-none range-detail-*` vs single-day rows). Rewiring those to a shared partial risks the FormPost layout. Since `_BuildStrikeRotaRow` is only ever needed in **InstantToggle** (the flat list, Task 9) and for the `ToggleDay` re-render (Task 7, always InstantToggle), this task only **creates** the partial and leaves the FormPost rendering of `_BuildStrikeRotaTable` completely untouched. Task 9 wires the partial into the InstantToggle branch. This makes Task 4 a pure add (no behavior change by construction).

- [ ] **Step 1: Create `_BuildStrikeRotaRow.cshtml`** — a single all-day day-row modeling one `ShiftDisplayItem`: a date cell, a fill bar/count cell, an action/status cell, and an avatars cell (mirroring the current single-day `<tr>` in `_BuildStrikeRotaTable`). The action/status cell branches on `Interaction`: `FormPost` → the read-only status badge exactly as the current single-day row (SignedUp / Full / NeedsHelp); `InstantToggle` → a `@* TODO(InstantToggle) *@` placeholder (filled in Task 8). Model: `BuildStrikeRotaRowViewModel`. Use 4 columns to match the Build/Strike table header (Date / Filled / Status / SignedUp).

- [ ] **Step 2: Do NOT modify `_BuildStrikeRotaTable.cshtml`** — FormPost rendering (range form, `confirmSignup-*` modal, per-rota `<script>`, `dateRanges` grouping, detail/single rows) stays exactly as today. The new partial is unused until Task 9. Build to confirm it compiles.

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx -v quiet`
Expected: Build succeeds.

- [ ] **Step 4: Run and eyeball** — `/Shifts` Build/Strike rotas + onboarding shift step render identically (range picker, expandable ranges, per-day status). No regression.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/Shared/_BuildStrikeRotaRow.cshtml src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml
git commit -m "refactor(shifts): extract _BuildStrikeRotaRow partial (no behavior change)"
```

---

## Chunk 3: `ToggleDay` endpoint (TDD)

Adds the AJAX endpoint + the row-build reuse + the extracted dietary predicate. The endpoint is exercised by `ShiftsControllerToggleDayTests` (NSubstitute, mirroring `ShiftsControllerNameGateTests`). No view change in this chunk — toggle buttons land in Chunk 4.

### Task 5: `ShiftBrowsePageBuilder.BuildRowAsync`

Reuse the page builder's per-shift item construction to build one row's model for re-render.

**Files:**
- Modify: `src/Humans.Web/Models/Shifts/ShiftBrowsePageBuilder.cs`
- Test: `tests/Humans.Web.Tests/Shifts/ShiftBrowsePageBuilderRowTests.cs` (new)

**Data path (verified):** `ShiftBrowsePageBuilder` is constructed with `(IShiftManagementService shiftManagement, ITeamServiceRead teamService)` — **no `IShiftView`**. Its per-shift items come from `shiftManagement.GetBrowseShiftsAsync(new ShiftBrowseQuery(es.Id, departmentId, fromDate, toDate, flags))` → `ShiftBrowseMapper.MapToDisplayItem(UrgentShift, EventSettings)` (`ShiftBrowseMapper.cs:21`, `internal static`, **already shared** — nothing to extract). `UrgentShift.Shift` carries `IsAllDay`. `BuildRowAsync` must use this same path, NOT `IShiftView`, and must resolve `EventSettings` itself (the builder's other methods get it from the request record; here resolve via `shiftManagement.GetActiveAsync()`).

- [ ] **Step 1: Write the failing test** — stub `IShiftManagementService.GetBrowseShiftsAsync` to return a single `UrgentShift` (all-day) and `GetActiveAsync` to return the `EventSettings`; pass a `signups` list containing a Confirmed signup for that shift. Assert `BuildRowAsync(...)` returns a `ShiftDisplayItem` with the expected `ConfirmedCount`/`Signups` and `IsSignedUp == true`. (Mirror the substitute setup from `ShiftBrowsePageBuilderPieSortTests`.)

- [ ] **Step 2: Run, expect fail** — `dotnet test tests/Humans.Web.Tests -v quiet --filter ShiftBrowsePageBuilderRowTests` → FAIL (method missing).

- [ ] **Step 3: Implement `BuildRowAsync`** — signature `BuildRowAsync(Guid shiftId, IReadOnlyList<ShiftSignup> userSignups, bool isPrivileged, CancellationToken ct)` returning `(ShiftDisplayItem Item, bool IsSignedUp, SignupStatus? Status)`:

```csharp
public async Task<(ShiftDisplayItem Item, bool IsSignedUp, SignupStatus? Status)> BuildRowAsync(
    Guid shiftId, IReadOnlyList<ShiftSignup> userSignups, bool isPrivileged, CancellationToken ct)
{
    // GetActiveAsync() is EventSettings? — guard for nullable + TreatWarningsAsErrors (mirrors ShiftsController.cs:50-51).
    var es = await shiftManagement.GetActiveAsync()
        ?? throw new InvalidOperationException("BuildRowAsync requires an active event.");
    var flags = ShiftBrowseQueryFlags.IncludeSignups;
    if (isPrivileged) flags |= ShiftBrowseQueryFlags.IncludeAdminOnly | ShiftBrowseQueryFlags.IncludeHidden;

    var shifts = await shiftManagement.GetBrowseShiftsAsync(new ShiftBrowseQuery(es.Id, null, null, null, flags));
    var urgent = shifts.First(u => u.Shift.Id == shiftId);
    var item = ShiftBrowseMapper.MapToDisplayItem(urgent, es);

    var (signedShiftIds, statuses) = ShiftSignupHelper.ResolveActiveStatuses(userSignups);
    return (item, signedShiftIds.Contains(shiftId), statuses.GetValueOrDefault(shiftId));
}
```

(Caches are already invalidated by `SignUp/BailAsync`, so `GetBrowseShiftsAsync` returns fresh counts. Pass the caller's already-fetched `userSignups` in rather than re-reading here — `ToggleDay` has them. Confirm `ShiftBrowseMapper` is reachable from this assembly; if `internal`, both live in `Humans.Web`, so it is.)

- [ ] **Step 4: Run, expect pass** — same filter → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/Shifts/ShiftBrowsePageBuilder.cs tests/Humans.Web.Tests/Shifts/ShiftBrowsePageBuilderRowTests.cs
git commit -m "feat(shifts): ShiftBrowsePageBuilder.BuildRowAsync for single-row re-render"
```

### Task 6: Extract the dietary predicate

`RedirectIfDietaryMissingAsync` mixes the *predicate* (qualifies-for-cantina + empty DietaryPreference) with a `RedirectToAction`. `ToggleDay` needs the predicate but a JSON-style redirect. Extract the predicate so it is not copied a third time (spec §Server step 3).

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`

- [ ] **Step 1: Add the predicate helper**

```csharp
// True when the shift qualifies for a cantina meal and the user has no DietaryPreference yet.
private async Task<bool> ShiftNeedsDietaryFirstAsync(UserInfo user, Guid shiftId)
{
    var shift = await shiftMgmt.GetShiftByIdAsync(shiftId);
    if (shift is null || !shift.QualifiesForCantinaMeal()) return false;
    return string.IsNullOrEmpty(user.Profile?.DietaryPreference);
}
```

- [ ] **Step 2: Route the existing redirect helper through it** — `RedirectIfDietaryMissingAsync` becomes: `if (!await ShiftNeedsDietaryFirstAsync(user, shiftId)) return null;` then the existing `SetInfo` + `RedirectToAction`. Behavior unchanged.

- [ ] **Step 3: Build + run existing dietary tests**

Run: `dotnet test tests/Humans.Web.Tests -v quiet --filter Dietary`
Expected: PASS (no behavior change).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftsController.cs
git commit -m "refactor(shifts): extract ShiftNeedsDietaryFirstAsync predicate"
```

### Task 7: `ToggleDay` action

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`
- Test: `tests/Humans.Web.Tests/Controllers/ShiftsControllerToggleDayTests.cs` (new)

- [ ] **Step 1: Write failing tests** (one method each):
  - `Toggle_WhenNotSignedUp_SignsUp_ReturnsRowWithSignedUpHeaderTrue` — user has no active signup; `SignUpAsync` returns `Ok(confirmed)`. Assert: result is a `PartialViewResult` for `_BuildStrikeRotaRow`/`_EventRotaRow`; `Response.Headers["X-Signed-Up"] == "true"`; `signupService.Received().SignUpAsync(userId, shiftId, …)`.
  - `Toggle_WhenSignedUp_Bails_ReturnsSignedUpHeaderFalse` — `GetByUserAsync` returns an active signup for the shift; assert `BailAsync(signup.Id, userId, …)` called and `X-Signed-Up == "false"`.
  - `Toggle_OnOverlapFail_ReturnsWarningToast_NoStateChange` — `SignUpAsync` returns `Fail("Time conflict …")`; assert `X-Toast-Type == "warning"`, `X-Toast-Msg` set, and the row is still rendered (unchanged state).
  - `Toggle_WhenDietaryMissing_Returns204WithRedirectHeader` — `ShiftNeedsDietaryFirstAsync` true; assert `StatusCode == 204` and `X-Redirect` points at `/Profile/DietaryMedical`.
  - `Toggle_WhenNameMissing_Returns204WithRedirectHeader` — name gate trips; assert 204 + `X-Redirect`.

  Use the `ShiftsControllerNameGateTests` constructor/claims scaffolding (copy the setup). Stub `ShiftBrowsePageBuilder.BuildRowAsync` indirectly via `IShiftView` substitute, or assert on the `PartialViewResult.ViewName`/`Model` without rendering.

- [ ] **Step 2: Run, expect fail** — `dotnet test tests/Humans.Web.Tests -v quiet --filter ToggleDay` → FAIL (action missing).

- [ ] **Step 3: Implement `ToggleDay`**

```csharp
[HttpPost("ToggleDay")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ToggleDay(Guid shiftId, CancellationToken ct)
{
    var (challenge, user) = await ResolveCurrentUserOrChallengeAsync();
    if (challenge is not null) return challenge;

    if (RedirectIfNameMissing(user) is not null)
        return RedirectHeader(Url.Action("Shifts", "OnboardingWidget")!); // 204 + X-Redirect

    if (await ShiftNeedsDietaryFirstAsync(user, shiftId))
        return RedirectHeader(Url.Action("DietaryMedical", "Profile",
            new { returnAction = "signup", shiftId })!);

    // Resolve existing active signup for this shift (need the signup id for bail).
    var signups = await signupService.GetByUserAsync(user.Id);
    var existing = signups.FirstOrDefault(s =>
        s.ShiftId == shiftId && s.Status is SignupStatus.Confirmed or SignupStatus.Pending);

    var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
    SignupResult result;
    bool signedUp;
    if (existing is not null)
    {
        result = await signupService.BailAsync(existing.Id, user.Id, "self-service toggle");
        signedUp = false;
    }
    else
    {
        result = await signupService.SignUpAsync(user.Id, shiftId,
            flags: privileged ? ShiftSignupRequestFlags.Privileged : ShiftSignupRequestFlags.None);
        signedUp = result.Success; // a Fail (overlap/capacity) leaves the user unsigned
    }

    // Re-read signups AFTER the change (caches were invalidated by SignUp/Bail): used
    // both for the fresh My-Shifts count and for the row's IsSignedUp/Status.
    var after = await signupService.GetByUserAsync(user.Id);
    var row = await browsePageBuilder.BuildRowAsync(shiftId, after, privileged, ct);
    var blocked = await ComputeSignupsBlockedByMissingDietaryAsync(user, ct);
    // EventSettings? guard (nullable + TreatWarningsAsErrors); browse is only reachable with an active event.
    var es = await shiftMgmt.GetActiveAsync()
        ?? throw new InvalidOperationException("ToggleDay requires an active event.");

    Response.Headers["X-Signed-Up"] = signedUp ? "true" : "false";
    Response.Headers["X-My-Signup-Count"] =
        after.Count(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending).ToString();
    if (!result.Success && result.Error is not null)
        SetToastHeader("warning", result.Error);
    else if (result.Warning is not null)
        SetToastHeader("warning", result.Warning);

    if (row.Item.Shift.IsAllDay)
        return PartialView("_BuildStrikeRotaRow", new BuildStrikeRotaRowViewModel
        {
            Item = row.Item, Es = es,
            IsSignedUp = row.IsSignedUp, SignupsBlockedByMissingDietary = blocked,
            Interaction = ShiftSignupInteraction.InstantToggle
        });

    return PartialView("_EventRotaRow", new EventRotaRowViewModel
    {
        Item = row.Item, Es = await shiftMgmt.GetActiveAsync(),
        IsSignedUp = row.IsSignedUp, SignupStatus = row.Status,
        SignupsBlockedByMissingDietary = blocked,
        Interaction = ShiftSignupInteraction.InstantToggle
    });
}
```

Helpers (private): `RedirectHeader(url)` sets `Response.Headers["X-Redirect"]` and returns `StatusCode(204)`; `SetToastHeader(type, msg)` sets `X-Toast-Type` and URL-encoded `X-Toast-Msg`. `ComputeSignupsBlockedByMissingDietaryAsync` already exists on the controller. `es` is resolved once with a null guard and reused for both row VMs; `GetActiveAsync()` is cached so the cost is negligible.

> Controller-purity (hard rules): `ToggleDay` only parses input, calls services + the page-builder, and formats the response (headers + partial). No repository access, no business logic.

- [ ] **Step 4: Run, expect pass** — `dotnet test tests/Humans.Web.Tests -v quiet --filter ToggleDay` → PASS.

- [ ] **Step 5: Full test sweep + commit**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all PASS.

```bash
git add src/Humans.Web/Controllers/ShiftsController.cs tests/Humans.Web.Tests/Controllers/ShiftsControllerToggleDayTests.cs
git commit -m "feat(shifts): ToggleDay endpoint for per-day instant signup"
```

---

## Chunk 4: InstantToggle rendering + client JS

Light up the toggle UI on the browse page. After this chunk, `/Shifts` toggles per-day with no reload; onboarding is still untouched.

### Task 8: Toggle button in the row partials

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_EventRotaRow.cshtml`, `_BuildStrikeRotaRow.cshtml`
- Add resource keys (Task 11)

- [ ] **Step 1: Fill the `InstantToggle` branch** in both row partials with the toggle button (replacing the TODO placeholder):

```razor
@{
    var disabled = Model.Item.RemainingSlots <= 0 && !Model.IsSignedUp
        || Model.SignupsBlockedByMissingDietary;
    var title = Model.SignupsBlockedByMissingDietary
        ? Localizer["Shifts_SignupDisabledTooltip_MissingDietary"].Value : null;
}
<button type="button"
        class="btn btn-sm js-day-toggle @(Model.IsSignedUp ? "btn-success" : "btn-outline-success")"
        data-shift-id="@Model.Item.Shift.Id"
        data-signed-up="@Model.IsSignedUp.ToString().ToLowerInvariant()"
        @(disabled ? "disabled" : "") title="@title">
    @if (Model.IsSignedUp) { <i class="fa-solid fa-check me-1"></i> @Localizer["Shifts_Toggle_Remove"] }
    else if (Model.Item.RemainingSlots <= 0) { <span class="badge bg-secondary">@Localizer["Shifts_Full"]</span> }
    else { <i class="fa-solid fa-plus me-1"></i> @Localizer["Shifts_Toggle_SignUp"] }
</button>
```

(For Build/Strike, the conflict heads-up `fa-triangle-exclamation` is advisory only; keep it out for v1 unless trivial — the block-and-warn happens server-side. If kept, render it when the user has another active signup overlapping this day, reusing the data the row already has.)

- [ ] **Step 2: Build + run** — confirm buttons render on `/Shifts` for both row types; onboarding still shows the legacy form.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shared/_EventRotaRow.cshtml src/Humans.Web/Views/Shared/_BuildStrikeRotaRow.cshtml
git commit -m "feat(shifts): toggle button in InstantToggle row rendering"
```

### Task 9: Flat per-day list + range-form gating in `_BuildStrikeRotaTable`

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml`

- [ ] **Step 1: Branch the table on `Interaction`:**
  - `FormPost` → unchanged: range form + modal + `dateRanges` grouping (expandable ranges) as today.
  - `InstantToggle` → render the range form/modal **not at all**; render a flat `<table>` iterating `allDayShifts` (ordered by `DayOffset`), one `_BuildStrikeRotaRow` per shift. No range headers, no expand rows.

- [ ] **Step 2: Build + run** — `/Shifts` Build/Strike rota shows a flat per-day list of toggle buttons; onboarding shows the range picker.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml
git commit -m "feat(shifts): flat per-day list for Build/Strike in InstantToggle mode"
```

### Task 10: `shifts.js` delegated handler + Index wiring

**Files:**
- Create: `src/Humans.Web/wwwroot/js/shifts.js`
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml`

- [ ] **Step 1: Write `shifts.js`** — one delegated `click` listener on `.js-day-toggle`:

```js
(function () {
  function token() {
    var el = document.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : '';
  }
  function showToast(type, msg) {
    var c = document.querySelector('.shifts-toast-container');
    if (!c || !msg) return;
    var div = document.createElement('div');
    div.className = 'alert alert-' + (type === 'error' ? 'danger' : type);
    div.textContent = msg;
    c.appendChild(div);
    setTimeout(function () { bootstrap.Alert.getOrCreateInstance(div).close(); }, 4000);
  }
  document.addEventListener('click', function (e) {
    var btn = e.target.closest('.js-day-toggle');
    if (!btn || btn.disabled) return;
    btn.disabled = true;
    var original = btn.innerHTML;
    btn.innerHTML = '<span class="spinner-border" style="width:14px;height:14px"></span>';
    var fd = new FormData();
    fd.append('__RequestVerificationToken', token());
    fd.append('shiftId', btn.dataset.shiftId);
    fetch('/Shifts/ToggleDay', { method: 'POST', headers: { 'X-Requested-With': 'XMLHttpRequest' }, body: fd })
      .then(function (r) {
        var redirect = r.headers.get('X-Redirect');
        if (redirect) { window.location = redirect; return null; }
        var toastType = r.headers.get('X-Toast-Type');
        var toastMsg = r.headers.get('X-Toast-Msg');
        var count = r.headers.get('X-My-Signup-Count');
        if (toastType && toastMsg) showToast(toastType, decodeURIComponent(toastMsg));
        if (count !== null) {
          var badge = document.querySelector('.shifts-nav-tabs .badge');
          if (badge) badge.textContent = count;
        }
        return r.text();
      })
      .then(function (html) {
        if (html === null || html === undefined) return;
        var row = btn.closest('tr');
        if (row) row.outerHTML = html; // swap the re-rendered row (re-enables the button)
      })
      .catch(function () {
        btn.disabled = false;
        btn.innerHTML = original;
        showToast('error', 'Something went wrong. Please try again.');
      });
  });
})();
```

- [ ] **Step 2: Reference it + set InstantToggle** — in `Index.cshtml`: add `<script src="~/js/shifts.js"></script>` (after Bootstrap); ensure a `__RequestVerificationToken` is present on the page (add `@Html.AntiForgeryToken()` once near the top of the container if not already). Set `Interaction = ShiftSignupInteraction.InstantToggle` on **all four** `BuildStrikeRotaTableViewModel`/`EventRotaTableViewModel` constructions (urgency view ~lines 388/414 and dept view ~lines 546/572).

- [ ] **Step 3: Run and exercise (required, per doctrine)** — `dotnet run --project src/Humans.Web`, dev-login, `/Shifts`:
  - Toggle an all-day day on → row flips to "You're on", count badge +1, no reload.
  - Toggle it off → reverts, badge −1.
  - Toggle a timed Event shift on/off.
  - Attempt an overlapping day → warning toast, row unchanged.
  - Confirm the My Shifts tab badge updates and onboarding wizard is unaffected.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/wwwroot/js/shifts.js src/Humans.Web/Views/Shifts/Index.cshtml
git commit -m "feat(shifts): client toggle handler + wire InstantToggle on browse page"
```

### Task 11: Resource strings

**Files:**
- Modify: the `SharedResource` `.resx` files (all locales — find via `grep -rl "Shifts_SignUpButton" src/Humans.Web/Resources` or wherever `SharedResource.*.resx` live).

- [ ] **Step 1: Add keys** to every locale `.resx`: `Shifts_Toggle_SignUp` ("Sign up"), `Shifts_Toggle_Remove` ("You're on — tap to remove"), and any toast strings not already covered. Reuse existing `Shifts_Full` / `Shifts_SignupDisabledTooltip_MissingDietary`. Translate for each supported EU locale (do not leave English placeholders — GDPR/locale completeness).

- [ ] **Step 2: Build + run** — labels resolve in each locale.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Resources
git commit -m "i18n(shifts): per-day toggle button + toast strings"
```

---

## Chunk 5: Instant removal everywhere + dead-surface deletion

### Task 12: Drop `data-confirm` on My Shifts bail

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Mine.cshtml`

- [ ] **Step 1:** Remove the two `data-confirm="…"` attributes on the bail buttons — `BailRange` (`Mine.cshtml:111`) and `Bail` (`Mine.cshtml:140`) — so removal is a single click. There is no `data-confirm` on the Pending-tab withdraw form (~:181); leave it alone. **Do not** touch the iCal "Regenerate URL" `data-confirm` (~:318) — that confirm is intentional and out of scope. Leave the forms/actions otherwise unchanged.
- [ ] **Step 2: Run** — My Shifts bail removes without a confirm dialog.
- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shifts/Mine.cshtml
git commit -m "feat(shifts): instant removal on My Shifts (drop confirm prompt)"
```

### Task 13: Delete dead `ShiftsController` actions (caller-checked)

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`

- [ ] **Step 1: Caller check** — confirm nothing still posts to `ShiftsController.SignUp` / `SignUpRange`:

```bash
grep -rn 'asp-action="SignUp"\|asp-action="SignUpRange"\|"SignUp"\|"SignUpRange"' src/Humans.Web/Views src/Humans.Web/Controllers
```

Expect: only the OnboardingWidget views/controller reference their own `SignUp`/`SignUpRange` (different controller), and `ProfileController` replays via the **service**, not these actions. Browse no longer references them. Use the `reforge` skill to confirm zero inbound references to the `ShiftsController` members if any ambiguity.

- [ ] **Step 2: Delete** `ShiftsController.SignUp`, `ShiftsController.SignUpRange`, `RedirectIfDietaryMissingForRangeAsync`, and `RedirectIfDietaryMissingAsync` **iff** the check confirms they are dead. Keep `ShiftNeedsDietaryFirstAsync` (used by `ToggleDay`). Do NOT touch `IShiftSignupService.SignUpRangeAsync` (live: OnboardingWidget + Profile replay).

- [ ] **Step 3: Build + full test sweep**

Run: `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet`
Expected: all PASS (architecture baseline tests included).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftsController.cs
git commit -m "refactor(shifts): remove dead ShiftsController signup/range actions"
```

### Task 14: Final verification + PR

- [ ] **Step 1: Full sweep** — `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet` → green.
- [ ] **Step 2: Manual smoke (required)** — re-run the Task 10 Step 3 checklist end-to-end on a fresh `dotnet run`. Optionally run `/test-site shifts`.
- [ ] **Step 3: Self-review for reuse/dead-surface** — run `/reuse-review` (or the `reuse-review` skill) over the diff; confirm no unintended new public surface beyond `ToggleDay` + the two row VMs + `BuildRowAsync`.
- [ ] **Step 4: Open PR to `origin/main`** — push `feat/per-day-shift-signup`; PR body summarizes the toggle, the OnboardingWidget-preserving `Interaction` split, and the dead-surface removal. Preview deploy at `https://{pr_id}.n.burn.camp` for Frank to click.

---

## Notes / risks

- **Double `GetByUserAsync`** in `ToggleDay` (once to find the signup, once to recount post-change) is fine at ~500 users; do not optimize prematurely.
- **Aggregate fill bars** (rota/department headers) stay stale until reload — documented non-goal. Per-row fill is live via the re-rendered row.
- **`X-Toast-Msg`** must be URL-encoded server-side and `decodeURIComponent`'d client-side (messages may contain commas/spaces; headers must be ASCII-safe).
- **Antiforgery:** ensure exactly one `__RequestVerificationToken` input exists on `/Shifts` once the range forms are gone from the browse render path.
- If `reforge`/grep shows a surviving caller of `ShiftsController.SignUp`/`SignUpRange`, **keep** the action and note it rather than force-deleting (hard rule: fix it right, don't break callers).
