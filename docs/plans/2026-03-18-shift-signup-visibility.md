# Shift Signup Visibility Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let coordinators and admins see who signed up for each shift — inline names for Event shifts, avatar rows for Build/Strike shifts.

**Architecture:** Add a `Signups` list to `ShiftDisplayItem` populated when the viewer is privileged. Service method gains an `includeSignups` parameter. Views conditionally render a "Signed Up" column based on a `ShowSignups` flag on the view model.

**Tech Stack:** ASP.NET Core MVC, Razor views, EF Core (no schema changes), existing `UserAvatarViewComponent`

**Spec:** `docs/features/26-shift-signup-visibility.md`

---

## Chunk 1: Data Layer + ViewModel

### Task 1: Add ShiftSignupInfo DTO and extend ShiftDisplayItem

**Files:**
- Modify: `src/Humans.Web/Models/ShiftViewModels.cs:154-162`

- [ ] **Step 1: Add ShiftSignupInfo record and Signups property**

In `ShiftViewModels.cs`, add the DTO before `ShiftDisplayItem` (around line 153), and add the `Signups` property to `ShiftDisplayItem`:

```csharp
public record ShiftSignupInfo(Guid UserId, string DisplayName, SignupStatus Status, string? ProfilePictureUrl);
```

Add to `ShiftDisplayItem`:
```csharp
public IReadOnlyList<ShiftSignupInfo> Signups { get; set; } = [];
```

- [ ] **Step 2: Add ShowSignups flag to ShiftBrowseViewModel**

In `ShiftBrowseViewModel` (line 131), add:
```csharp
public bool ShowSignups { get; set; }
```

- [ ] **Step 3: Build and verify no errors**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Commit**

```
git add src/Humans.Web/Models/ShiftViewModels.cs
git commit -m "feat(shifts): add ShiftSignupInfo DTO and ShowSignups flag to view models"
```

---

### Task 2: Extend service to include signup data

**Files:**
- Modify: `src/Humans.Application/Interfaces/IShiftManagementService.cs` — `UrgentShift` record
- Modify: `src/Humans.Infrastructure/Services/ShiftManagementService.cs` — `GetBrowseShiftsAsync`

- [ ] **Step 1: Add Signups to UrgentShift record**

In `IShiftManagementService.cs`, extend the `UrgentShift` record (currently at ~line 184-189) to include signups:

```csharp
public record UrgentShift(
    Shift Shift,
    double UrgencyScore,
    int ConfirmedCount,
    int RemainingSlots,
    string DepartmentName,
    IReadOnlyList<(Guid UserId, string DisplayName, SignupStatus Status, bool HasProfilePicture)> Signups);
```

- [ ] **Step 2: Update GetBrowseShiftsAsync to accept includeSignups parameter**

In the interface, change the signature:
```csharp
Task<IReadOnlyList<UrgentShift>> GetBrowseShiftsAsync(
    Guid eventSettingsId, Guid? departmentId = null, LocalDate? date = null,
    bool includeAdminOnly = false, bool includeSignups = false);
```

- [ ] **Step 3: Update the implementation in ShiftManagementService**

In `ShiftManagementService.GetBrowseShiftsAsync` (~line 483-521):

1. Change the existing `.Include(s => s.ShiftSignups)` to `.Include(s => s.ShiftSignups).ThenInclude(ss => ss.User)` unconditionally. At ~500-user scale, the extra User join is negligible and avoids conditional include complexity.

2. In the projection to `UrgentShift`, add the Signups field:

```csharp
Signups: includeSignups
    ? shift.ShiftSignups
        .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
        .Select(ss => (ss.UserId, ss.User.DisplayName, ss.Status,
            HasProfilePicture: ss.User.ProfilePictureUrl != null))
        .OrderBy(ss => ss.Status) // Confirmed first, then Pending
        .ThenBy(ss => ss.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList()
    : []
```

- [ ] **Step 4: Fix ALL existing UrgentShift constructions**

The `UrgentShift` record now has 7 fields. Every `new UrgentShift(...)` call must include a `Signups` argument. Update ALL of these:

1. In `GetBrowseShiftsAsync` (~line 517): add `Signups: [computed above]` or `Signups: []`
2. In `GetUrgentShiftsAsync` (~line 471): add `Signups: []` as the last argument — this method is used by the dashboard and homepage, which don't need signup data

Grep for `new UrgentShift(` to find all construction sites and fix each one.

- [ ] **Step 5: Build and verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 6: Commit**

```
git add src/Humans.Application/Interfaces/IShiftManagementService.cs src/Humans.Infrastructure/Services/ShiftManagementService.cs
git commit -m "feat(shifts): include signup user data in browse query when privileged"
```

---

### Task 3: Wire up controller to pass signup data to view

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs:72-147`

- [ ] **Step 1: Pass includeSignups to GetBrowseShiftsAsync**

At line 72-73, change the call:
```csharp
var urgentShifts = await _shiftMgmt.GetBrowseShiftsAsync(
    es.Id, departmentId: departmentId, date: filterDate,
    includeAdminOnly: isPrivileged, includeSignups: isPrivileged);
```

Also at line 129 (the unfiltered call for the department dropdown):
```csharp
var allShifts = await _shiftMgmt.GetBrowseShiftsAsync(es.Id,
    includeAdminOnly: isPrivileged, includeSignups: false);
```
(No signups needed for the dropdown query.)

- [ ] **Step 2: Map UrgentShift.Signups to ShiftDisplayItem.Signups**

In the projection at lines 96-106, add mapping:
```csharp
return new ShiftDisplayItem
{
    Shift = u.Shift,
    AbsoluteStart = start,
    AbsoluteEnd = end,
    Period = period,
    ConfirmedCount = u.ConfirmedCount,
    RemainingSlots = u.RemainingSlots,
    Signups = u.Signups
        .Select(s => new ShiftSignupInfo(
            s.UserId, s.DisplayName, s.Status,
            s.HasProfilePicture ? $"/Human/{s.UserId}/Picture" : null))
        .ToList()
};
```

- [ ] **Step 3: Set ShowSignups on the view model**

At line 137-147, add the flag:
```csharp
var model = new ShiftBrowseViewModel
{
    // ... existing properties ...
    ShowSignups = isPrivileged
};
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 5: Commit**

```
git add src/Humans.Web/Controllers/ShiftsController.cs
git commit -m "feat(shifts): pass signup data to browse view for privileged users"
```

---

## Chunk 2: Views + Localization

### Task 4: Add "Signed Up" column to Event shifts on browse page

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml:196-249`

- [ ] **Step 1: Add column header**

At line 207, change:
```html
<th></th>
```
to (conditionally add the Signups column):
```html
@if (Model.ShowSignups)
{
    <th>@Localizer["Shifts_SignedUp"]</th>
}
<th></th>
```

- [ ] **Step 2: Add signup names cell in the row loop**

After the Filled `</td>` (line 229), before the action `<td>` (line 230), add:
```html
@if (Model.ShowSignups)
{
    <td class="small">
        @foreach (var signup in item.Signups)
        {
            if (signup != item.Signups[0]) { <text>, </text> }
            <a asp-controller="Human" asp-action="View" asp-route-id="@signup.UserId"
               class="text-decoration-none @(signup.Status == SignupStatus.Pending ? "text-muted fst-italic" : "")">@signup.DisplayName</a>@if (signup.Status == SignupStatus.Pending)
            {<small class="text-muted"> @Localizer["Shifts_Pending"]</small>}
        }
    </td>
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build Humans.slnx`

- [ ] **Step 4: Commit**

```
git add src/Humans.Web/Views/Shifts/Index.cshtml
git commit -m "feat(shifts): add signup names column for Event shifts on browse page"
```

---

### Task 5: Add "Signed Up" avatar column to Build/Strike shifts on browse page

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml:125-163`

- [ ] **Step 1: Add column header**

At line 133, change:
```html
<tr><th>Date</th><th>Filled</th><th>Status</th></tr>
```
to:
```html
<tr><th>Date</th><th>Filled</th><th>Status</th>@if (Model.ShowSignups) {<th>@Localizer["Shifts_SignedUp"]</th>}</tr>
```

- [ ] **Step 2: Add avatar row cell**

After the Status `</td>` (line 159), before `</tr>` (line 160), add:
```html
@if (Model.ShowSignups)
{
    <td>
        <div class="d-flex flex-wrap gap-1">
            @foreach (var signup in item.Signups)
            {
                <a asp-controller="Human" asp-action="View" asp-route-id="@signup.UserId"
                   title="@signup.DisplayName@(signup.Status == SignupStatus.Pending ? $" ({Localizer["Shifts_Pending"]})" : "")"
                   class="@(signup.Status == SignupStatus.Pending ? "opacity-50" : "")"
                   style="@(signup.Status == SignupStatus.Pending ? "border: 1px dashed #6c757d; border-radius: 50%;" : "")">
                    <vc:user-avatar profile-picture-url="@signup.ProfilePictureUrl"
                                    display-name="@signup.DisplayName"
                                    size="26" />
                </a>
            }
        </div>
    </td>
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build Humans.slnx`

- [ ] **Step 4: Commit**

```
git add src/Humans.Web/Views/Shifts/Index.cshtml
git commit -m "feat(shifts): add signup avatar column for Build/Strike shifts on browse page"
```

---

### Task 6: Add signup visibility to ShiftAdmin page

**Files:**
- Modify: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`

The admin page already loads `shift.ShiftSignups` with User navigation for past shifts. The change is to also render signups inline for future/current shifts.

- [ ] **Step 0: Ensure User navigation is loaded for all signups**

In `ShiftAdminController`, find where rotas/shifts are loaded (the `GetByShiftAsync` or similar call). Verify that `.Include(s => s.ShiftSignups).ThenInclude(ss => ss.User)` is present for ALL shifts, not just past ones. If the `User` include is missing or conditional on `isPast`, make it unconditional. Without this, `ss.User.DisplayName` will throw `NullReferenceException` for future shift signups.

- [ ] **Step 1: Add column header to the shift table**

Find the shift table `<thead>` row (around line 168-177). After the "Filled" `<th>`, add:
```html
@if (Model.CanApproveSignups)
{
    <th>@Localizer["Shifts_SignedUp"]</th>
}
```

- [ ] **Step 2: Add signup cell for each shift row**

In the shift row loop, after the Filled `<td>`, add a new `<td>`. The admin page has access to `shift.ShiftSignups` navigation property. Determine the rota period from the parent rota.

For Event rotas — show name list:
```html
@if (Model.CanApproveSignups)
{
    var activeSignups = shift.ShiftSignups
        .Where(ss => ss.Status is SignupStatus.Confirmed or SignupStatus.Pending)
        .OrderBy(ss => ss.Status).ThenBy(ss => ss.User.DisplayName, StringComparer.Ordinal);
    <td class="small">
        @if (rota.Period == RotaPeriod.Event)
        {
            @foreach (var ss in activeSignups)
            {
                if (ss != activeSignups.First()) { <text>, </text> }
                <a asp-controller="Human" asp-action="View" asp-route-id="@ss.UserId"
                   class="text-decoration-none @(ss.Status == SignupStatus.Pending ? "text-muted fst-italic" : "")">@ss.User.DisplayName</a>@if (ss.Status == SignupStatus.Pending)
                {<small class="text-muted"> @Localizer["Shifts_Pending"]</small>}
            }
        }
        else
        {
            <div class="d-flex flex-wrap gap-1">
                @foreach (var ss in activeSignups)
                {
                    <a asp-controller="Human" asp-action="View" asp-route-id="@ss.UserId"
                       title="@ss.User.DisplayName@(ss.Status == SignupStatus.Pending ? $" ({Localizer["Shifts_Pending"]})" : "")"
                       class="@(ss.Status == SignupStatus.Pending ? "opacity-50" : "")"
                       style="@(ss.Status == SignupStatus.Pending ? "border: 1px dashed #6c757d; border-radius: 50%;" : "")">
                        <vc:user-avatar profile-picture-url="@(ss.User.ProfilePictureUrl != null ? $"/Human/{ss.UserId}/Picture" : null)"
                                        display-name="@ss.User.DisplayName"
                                        size="26" />
                    </a>
                }
            </div>
        }
    </td>
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build Humans.slnx`

- [ ] **Step 4: Commit**

```
git add src/Humans.Web/Views/ShiftAdmin/Index.cshtml
git commit -m "feat(shifts): add signup visibility for future shifts on admin page"
```

---

### Task 7: Add localization keys

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.es.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.de.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.fr.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.it.resx`

- [ ] **Step 1: Add keys to all 5 locale files**

| Key | en | es | de | fr | it |
|-----|----|----|----|----|-----|
| `Shifts_SignedUp` | Signed Up | Apuntados | Angemeldet | Inscrits | Iscritti |
| `Shifts_Pending` | (pending) | (pendiente) | (ausstehend) | (en attente) | (in attesa) |

Add as `<data>` entries in each `.resx` file.

- [ ] **Step 2: Build and verify**

Run: `dotnet build Humans.slnx`

- [ ] **Step 3: Commit**

```
git add src/Humans.Web/Resources/SharedResource*.resx
git commit -m "feat(shifts): add localization keys for signup visibility"
```

---

### Task 8: Verify using @SignupStatus enum requires correct using

**Files:**
- Verify: `src/Humans.Web/Views/Shifts/Index.cshtml`
- Verify: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`

- [ ] **Step 1: Check _ViewImports.cshtml includes the Enums namespace**

The views reference `SignupStatus.Pending` and `SignupStatus.Confirmed`. Verify that `@using Humans.Domain.Enums` is in `Views/_ViewImports.cshtml`. If not, add it.

- [ ] **Step 2: Full build + run tests**

Run: `dotnet build Humans.slnx && dotnet test Humans.slnx`
Expected: All pass

- [ ] **Step 3: Commit if _ViewImports was changed**

```
git add src/Humans.Web/Views/_ViewImports.cshtml
git commit -m "fix(shifts): add Enums namespace to _ViewImports for signup status checks"
```

---

### Task 9: Manual QA verification

- [ ] **Step 1: Push and deploy to QA**

```
git push origin main
bash /opt/docker/human/deploy-qa.sh
```

- [ ] **Step 2: Test as Admin**

1. Navigate to `/Shifts` — verify "Signed Up" column appears for both Event and Build/Strike rotas
2. Event shifts: names should be comma-separated, linked to profiles, pending names italic
3. Build/Strike shifts: avatar row, confirmed full-opacity, pending semi-transparent with dashed border
4. Navigate to `/Teams/{slug}/Shifts` — verify same visibility on admin page

- [ ] **Step 3: Test as regular volunteer**

Log in as non-coordinator. Verify the "Signed Up" column does NOT appear on `/Shifts`. Only fill counts visible.

- [ ] **Step 4: Update feature spec if needed**

Check `docs/features/26-shift-signup-visibility.md` — update if any acceptance criteria changed during implementation.
