# Session 3: Shift UI Polish + Public Legal Section — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement shift UI polish (#184, #180, #176) and a public legal document section (#194).

**Architecture:** Batch 5 is view/label changes plus a new service method for range voluntell. Batch 9 extracts a reusable `LegalDocumentService` from GovernanceController, creates a new public `LegalController`, and adds navigation links.

**Tech Stack:** ASP.NET Core 9, EF Core, Razor views, xUnit + NSubstitute + AwesomeAssertions, NodaTime, Octokit (GitHub API)

**Spec:** `docs/superpowers/specs/2026-03-23-session3-shift-polish-legal-section.md`

**Issues:** #184, #180, #176, #194

---

## File Map

### Batch 5: Shift UI Polish

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `src/Humans.Web/Views/Shifts/Index.cshtml` | #184: heading + button text; #180: period headers + labeled date pickers |
| Modify | `src/Humans.Web/Views/Shifts/Mine.cshtml` | #184: "Browse Shifts" link text |
| Modify | `src/Humans.Web/Views/Home/_ShiftCards.cshtml` | #184: "Browse Shifts" button text |
| Modify | `src/Humans.Web/Views/Vol/Register.cshtml` | #184: "Browse Shifts" link text |
| Modify | `src/Humans.Web/Views/Team/Details.cshtml` | #184: remove duplicate shift link, always show shifts card |
| Modify | `src/Humans.Web/Views/Shared/_ShiftsSummaryCard.cshtml` | #184: handle empty summary state |
| Modify | `src/Humans.Web/Controllers/TeamController.cs` | #184: always provide ShiftsSummary for departments |
| Modify | `src/Humans.Infrastructure/Services/TeamPageService.cs` | #184: always return summary for departments |
| Modify | `src/Humans.Infrastructure/Services/ShiftSignupService.cs` | #176: add VoluntellRangeAsync |
| Modify | `src/Humans.Application/Interfaces/IShiftSignupService.cs` | #176: add VoluntellRangeAsync to interface |
| Modify | `src/Humans.Web/Controllers/ShiftAdminController.cs` | #176: add VoluntellRange action |
| Modify | `src/Humans.Web/Views/ShiftAdmin/Index.cshtml` | #176: range voluntell form UI |
| Modify | `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs` | #176: tests for VoluntellRangeAsync |

### Batch 9: Public Legal Section

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/Humans.Application/Interfaces/ILegalDocumentService.cs` | Interface + LegalDocumentDefinition record |
| Create | `src/Humans.Infrastructure/Services/LegalDocumentService.cs` | GitHub-fetching impl with per-doc caching |
| Create | `src/Humans.Web/Controllers/LegalController.cs` | Public legal page controller |
| Create | `src/Humans.Web/Models/LegalPageViewModel.cs` | View model for legal page |
| Create | `src/Humans.Web/Views/Legal/Index.cshtml` | Pill nav + full-page tabbed doc viewer |
| Create | `tests/Humans.Application.Tests/Services/LegalDocumentServiceTests.cs` | Service tests |
| Modify | `src/Humans.Web/Controllers/GovernanceController.cs` | Refactor to use ILegalDocumentService |
| Modify | `src/Humans.Application/CacheKeys.cs` | Add Legal document cache key pattern |
| Modify | `src/Humans.Web/Program.cs` | Register ILegalDocumentService |
| Modify | `src/Humans.Web/Authorization/MembershipRequiredFilter.cs` | Exempt LegalController |
| Modify | `src/Humans.Web/Views/Shared/_Layout.cshtml` | Add public "Legal" nav link |
| Modify | `src/Humans.Web/Views/Shared/_LoginPartial.cshtml` | Add "Legal" to profile dropdown |

---

## Task 1: Shift UI Text/Label Cleanup (#184)

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml:37,147`
- Modify: `src/Humans.Web/Views/Shifts/Mine.cshtml:39`
- Modify: `src/Humans.Web/Views/Home/_ShiftCards.cshtml:47`
- Modify: `src/Humans.Web/Views/Vol/Register.cshtml:51`
- Modify: `src/Humans.Web/Views/Team/Details.cshtml:299-301,334-339`
- Modify: `src/Humans.Web/Views/Shared/_ShiftsSummaryCard.cshtml`
- Modify: `src/Humans.Infrastructure/Services/TeamPageService.cs:137-173`
- Modify: `src/Humans.Web/Controllers/TeamController.cs:202-217`

- [ ] **Step 1: Change "Browse Shifts" → "Browse Volunteer Options" in Shifts/Index.cshtml**

In `src/Humans.Web/Views/Shifts/Index.cshtml`, line 37, change:
```html
<h2>Browse Shifts</h2>
```
to:
```html
<h2>Browse Volunteer Options</h2>
```

- [ ] **Step 2: Change "Sign Up for Range" button text in Shifts/Index.cshtml**

In `src/Humans.Web/Views/Shifts/Index.cshtml`, line 147, change:
```html
<button type="submit" class="btn btn-sm btn-success">Sign Up for Range</button>
```
to a period-specific label (this form only appears for Build/Strike rotas):
```html
<button type="submit" class="btn btn-sm btn-success">
    Sign up for @(rotaGroup.Rota.Period == RotaPeriod.Build ? "set-up" : "strike") dates
</button>
```
Note: Task 2 will group rotas by period, but this button text works regardless since the `isBuildStrike` check already limits it to Build/Strike.

- [ ] **Step 3: Change "Browse Shifts" in Mine.cshtml**

In `src/Humans.Web/Views/Shifts/Mine.cshtml`, line 39, change:
```html
<a asp-action="Index" class="btn btn-outline-primary">Browse Shifts</a>
```
to:
```html
<a asp-action="Index" class="btn btn-outline-primary">Browse Volunteer Options</a>
```

- [ ] **Step 4: Change "Browse Shifts" in _ShiftCards.cshtml**

In `src/Humans.Web/Views/Home/_ShiftCards.cshtml`, line 47, change:
```html
<a asp-controller="Shifts" asp-action="Index" class="btn btn-sm btn-outline-success">Browse Shifts</a>
```
to:
```html
<a asp-controller="Shifts" asp-action="Index" class="btn btn-sm btn-outline-success">Browse Volunteer Options</a>
```

- [ ] **Step 5: Change "Browse Shifts" in Register.cshtml**

In `src/Humans.Web/Views/Vol/Register.cshtml`, line 51, change `Browse Shifts` to `Browse Volunteer Options` (keep the icon).

- [ ] **Step 6: Remove duplicate shift link from Team Management card**

In `src/Humans.Web/Views/Team/Details.cshtml`, delete lines 334-339:
```html
@if (Model.ParentTeam == null && !Model.IsSystemTeam)
{
    <a asp-controller="ShiftAdmin" asp-action="Index" asp-route-slug="@Model.Slug" class="list-group-item list-group-item-action">
        @Localizer["Nav_Shifts"]
    </a>
}
```

- [ ] **Step 7: Make Shifts card always show for departments**

In `src/Humans.Infrastructure/Services/TeamPageService.cs`, modify `GetShiftsSummaryAsync()` (around line 137) to return a summary even when no shifts exist for departments. If the team is a non-child, non-system team and the user is authenticated, return a `TeamPageShiftsSummary` with zero counts instead of `null`.

- [ ] **Step 8: Update _ShiftsSummaryCard to handle empty state**

In `src/Humans.Web/Views/Shared/_ShiftsSummaryCard.cshtml`, wrap the progress bar and stats in a conditional so they only show when `Model.TotalSlots > 0`. The "Manage Shifts" button should always show when `Model.CanManageShifts`:

```html
@if (Model.TotalSlots > 0)
{
    @* existing progress bar and stats (lines 8-27) *@
}
@if (Model.CanManageShifts)
{
    <a href="@Model.ShiftsUrl" class="btn btn-sm btn-outline-primary mt-2 w-100">Manage Shifts</a>
}
```

- [ ] **Step 9: Build and verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeds with no errors.

- [ ] **Step 10: Commit**

```bash
git add src/Humans.Web/Views/Shifts/Index.cshtml src/Humans.Web/Views/Shifts/Mine.cshtml \
  src/Humans.Web/Views/Home/_ShiftCards.cshtml src/Humans.Web/Views/Vol/Register.cshtml \
  src/Humans.Web/Views/Team/Details.cshtml src/Humans.Web/Views/Shared/_ShiftsSummaryCard.cshtml \
  src/Humans.Infrastructure/Services/TeamPageService.cs src/Humans.Web/Controllers/TeamController.cs
git commit -m "fix: shift UI text/label cleanup and always show shifts card (#184)"
```

---

## Task 2: Clarify Rota Period Separation (#180)

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml:90-170`

- [ ] **Step 1: Group rotas by period and add section headers**

In `src/Humans.Web/Views/Shifts/Index.cshtml`, inside the department card body (around line 94), change from iterating `dept.Rotas` directly to grouping by period first:

```csharp
@{
    var rotasByPeriod = dept.Rotas
        .GroupBy(r => r.Rota.Period)
        .OrderBy(g => g.Key) // Build=0, Event=1, Strike=2
        .ToList();
}
@foreach (var periodGroup in rotasByPeriod)
{
    var periodName = periodGroup.Key == RotaPeriod.Build ? "Set-up"
        : periodGroup.Key == RotaPeriod.Strike ? "Strike"
        : "Event";
    <h5 class="mt-4 mb-2 border-bottom pb-1">
        <span class="badge @(periodGroup.Key == RotaPeriod.Build ? "bg-info" : periodGroup.Key == RotaPeriod.Strike ? "bg-secondary" : "bg-success") me-1">@periodName</span>
        @periodName
    </h5>
    @foreach (var rotaGroup in periodGroup)
    {
        @* existing rota rendering code *@
    }
}
```

- [ ] **Step 2: Add empty period note**

When a period group has rotas but all shifts are full or unavailable, show a note. Inside each period group's `@foreach`, after the rota rendering, add a check: if no rota in the group has available shifts, display:

```html
@{
    var hasAvailable = periodGroup.Any(r => r.Shifts.Any(s => s.RemainingSlots > 0));
}
@if (!hasAvailable)
{
    <p class="text-muted fst-italic">All @periodName.ToLower() shifts are full.</p>
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Shifts/Index.cshtml
git commit -m "feat: add period section headers and labeled date pickers (#180)"
```

---

## Task 3: Batch Voluntell Service Method + Tests (#176)

**Files:**
- Modify: `src/Humans.Application/Interfaces/IShiftSignupService.cs`
- Modify: `src/Humans.Infrastructure/Services/ShiftSignupService.cs`
- Modify: `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs`

- [ ] **Step 1: Write failing test — VoluntellRange creates confirmed signups across date range**

In `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs`, add:

```csharp
[Fact]
public async Task VoluntellRange_CreatesConfirmedSignupsAcrossDateRange()
{
    var (es, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
    // Seed 3 consecutive all-day shifts
    var shift1 = SeedAllDayShift(rota, dayOffset: 0);
    var shift2 = SeedAllDayShift(rota, dayOffset: 1);
    var shift3 = SeedAllDayShift(rota, dayOffset: 2);
    var volunteerId = Guid.NewGuid();
    var enrollerId = Guid.NewGuid();
    await _dbContext.SaveChangesAsync();

    var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, 0, 2, enrollerId);

    result.Success.Should().BeTrue();
    var signups = await _dbContext.ShiftSignups
        .Where(s => s.UserId == volunteerId)
        .ToListAsync();
    signups.Should().HaveCount(3);
    signups.Should().AllSatisfy(s =>
    {
        s.Status.Should().Be(SignupStatus.Confirmed);
        s.Enrolled.Should().BeTrue();
        s.EnrolledByUserId.Should().Be(enrollerId);
    });
    // All should share a SignupBlockId
    signups.Select(s => s.SignupBlockId).Distinct().Should().HaveCount(1);
}
```

Note: you may need to add a `SeedAllDayShift` helper if one doesn't exist. Check the existing test helpers in the file — look for how `SeedShiftScenario` creates shifts and replicate the pattern for multiple all-day shifts.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Humans.Application.Tests --filter "VoluntellRange_CreatesConfirmedSignupsAcrossDateRange" -v n`
Expected: FAIL — `VoluntellRangeAsync` method does not exist.

- [ ] **Step 3: Write failing test — VoluntellRange skips existing signups**

```csharp
[Fact]
public async Task VoluntellRange_SkipsShiftsWhereUserAlreadySignedUp()
{
    var (es, rota, _) = SeedShiftScenario(SignupPolicy.RequireApproval);
    var shift1 = SeedAllDayShift(rota, dayOffset: 0);
    var shift2 = SeedAllDayShift(rota, dayOffset: 1);
    var volunteerId = Guid.NewGuid();
    var enrollerId = Guid.NewGuid();
    // Pre-existing signup on shift1
    _dbContext.ShiftSignups.Add(new ShiftSignup
    {
        Id = Guid.NewGuid(),
        UserId = volunteerId,
        ShiftId = shift1.Id,
        Status = SignupStatus.Confirmed,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    });
    await _dbContext.SaveChangesAsync();

    var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, 0, 1, enrollerId);

    result.Success.Should().BeTrue();
    var newSignups = await _dbContext.ShiftSignups
        .Where(s => s.UserId == volunteerId && s.EnrolledByUserId == enrollerId)
        .ToListAsync();
    newSignups.Should().HaveCount(1);
    newSignups[0].ShiftId.Should().Be(shift2.Id);
}
```

- [ ] **Step 4: Write failing test — VoluntellRange returns error for invalid rota**

```csharp
[Fact]
public async Task VoluntellRange_ReturnsError_WhenRotaNotFound()
{
    var volunteerId = Guid.NewGuid();
    var enrollerId = Guid.NewGuid();

    var result = await _service.VoluntellRangeAsync(volunteerId, Guid.NewGuid(), 0, 2, enrollerId);

    result.Success.Should().BeFalse();
    result.Error.Should().NotBeNullOrEmpty();
}
```

- [ ] **Step 5: Add VoluntellRangeAsync to IShiftSignupService interface**

In `src/Humans.Application/Interfaces/IShiftSignupService.cs`, add:

```csharp
Task<SignupResult> VoluntellRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId);
```

- [ ] **Step 6: Implement VoluntellRangeAsync in ShiftSignupService**

In `src/Humans.Infrastructure/Services/ShiftSignupService.cs`, add the method after `VoluntellAsync`:

```csharp
public async Task<SignupResult> VoluntellRangeAsync(
    Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId)
{
    var rota = await _dbContext.Rotas
        .Include(r => r.Shifts)
        .FirstOrDefaultAsync(r => r.Id == rotaId);

    if (rota is null)
        return SignupResult.Fail("Rota not found.");

    var shiftsInRange = rota.Shifts
        .Where(s => s.IsAllDay && s.DayOffset >= startDayOffset && s.DayOffset <= endDayOffset)
        .OrderBy(s => s.DayOffset)
        .ToList();

    if (shiftsInRange.Count == 0)
        return SignupResult.Fail("No shifts found in the specified range.");

    // Check for existing signups to skip
    var existingSignupShiftIds = await _dbContext.ShiftSignups
        .Where(s => s.UserId == userId
            && shiftsInRange.Select(sh => sh.Id).Contains(s.ShiftId)
            && (s.Status == SignupStatus.Confirmed || s.Status == SignupStatus.Pending))
        .Select(s => s.ShiftId)
        .ToListAsync();

    var shiftsToAssign = shiftsInRange
        .Where(s => !existingSignupShiftIds.Contains(s.Id))
        .ToList();

    if (shiftsToAssign.Count == 0)
        return SignupResult.Fail("Volunteer is already signed up for all shifts in this range.");

    var now = _clock.GetCurrentInstant();
    var blockId = Guid.NewGuid();

    foreach (var shift in shiftsToAssign)
    {
        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shift.Id,
            Status = SignupStatus.Confirmed,
            Enrolled = true,
            EnrolledByUserId = enrollerUserId,
            ReviewedByUserId = enrollerUserId,
            ReviewedAt = now,
            SignupBlockId = blockId,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.ShiftSignups.Add(signup);
    }

    await _dbContext.SaveChangesAsync();

    // SignupResult.Ok takes a ShiftSignup — return the first one created
    var firstSignup = await _dbContext.ShiftSignups
        .FirstAsync(s => s.SignupBlockId == blockId);
    return SignupResult.Ok(firstSignup);
}
```

Note: Verify the exact `SignupResult.Ok` signature — it takes a `ShiftSignup` parameter. If the caller needs to know how many shifts were assigned, the success message in the controller can say "Volunteer assigned to {count} shifts."

- [ ] **Step 7: Run all tests to verify they pass**

Run: `dotnet test tests/Humans.Application.Tests --filter "VoluntellRange" -v n`
Expected: All 3 tests PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/Interfaces/IShiftSignupService.cs \
  src/Humans.Infrastructure/Services/ShiftSignupService.cs \
  tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs
git commit -m "feat: add VoluntellRangeAsync for batch shift assignment (#176)"
```

---

## Task 4: Batch Voluntell Controller + Admin UI (#176)

**Depends on:** Task 3

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftAdminController.cs`
- Modify: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`

- [ ] **Step 1: Add VoluntellRange action to ShiftAdminController**

In `src/Humans.Web/Controllers/ShiftAdminController.cs`, add after the existing `Voluntell` action (around line 526):

```csharp
[HttpPost("VoluntellRange")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> VoluntellRange(string slug, Guid rotaId, int startDayOffset, int endDayOffset, Guid userId)
{
    var (teamError, currentUser, team) = await ResolveDepartmentApprovalAsync(slug);
    if (teamError is not null) return teamError;

    var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
    if (rota is null) return NotFound();
    if (rota.TeamId != team.Id) return NotFound();

    var result = await _signupService.VoluntellRangeAsync(userId, rotaId, startDayOffset, endDayOffset, currentUser.Id);
    if (result.Success)
    {
        SetSuccess("Volunteer assigned to shift range.");
    }
    else
    {
        SetError(result.Error ?? "Range assignment failed.");
    }

    return RedirectToAction(nameof(Index), new { slug });
}
```

Note: Check if `_shiftMgmt.GetRotaByIdAsync` exists. If not, use the pattern from the existing `Voluntell` action to validate the rota belongs to the team. You may need to query the rota through `_dbContext` or an existing service method.

- [ ] **Step 2: Add range voluntell form to ShiftAdmin view**

In `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`, for Build/Strike rotas only, add a range voluntell form at the top of each rota section (before the shift table). Place it after the rota header and description, gated on `Model.CanApproveSignups`. Use the same pattern as the volunteer-facing range signup but with an additional volunteer search field:

```html
@if (Model.CanApproveSignups && isBuildStrike)
{
    var availableAdminShifts = allDayShifts.Where(s => !s.IsPast).ToList();
    if (availableAdminShifts.Count > 0)
    {
        var rangeFormId = $"voluntell-range-{rota.Id}";
        <div class="card card-body bg-light mb-3">
            <form asp-action="VoluntellRange" method="post" class="row g-2 align-items-end" id="@rangeFormId">
                @Html.AntiForgeryToken()
                <input type="hidden" name="rotaId" value="@rota.Id" />
                <div class="col-auto">
                    <label class="form-label form-label-sm">Start</label>
                    <select name="startDayOffset" class="form-select form-select-sm">
                        @foreach (var s in availableAdminShifts)
                        {
                            <option value="@s.Shift.DayOffset">@es.GateOpeningDate.PlusDays(s.Shift.DayOffset).ToDisplayShiftDate()</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label form-label-sm">End</label>
                    <select name="endDayOffset" class="form-select form-select-sm">
                        @foreach (var s in availableAdminShifts)
                        {
                            <option value="@s.Shift.DayOffset" selected="@(s == availableAdminShifts.Last() ? "selected" : null)">@es.GateOpeningDate.PlusDays(s.Shift.DayOffset).ToDisplayShiftDate()</option>
                        }
                    </select>
                </div>
                <div class="col-auto">
                    <label class="form-label form-label-sm">Volunteer</label>
                    <input type="text" class="form-control form-control-sm voluntell-range-search"
                           data-rota-id="@rota.Id"
                           placeholder="Search by name (min 2 chars)..." />
                    <input type="hidden" name="userId" class="voluntell-range-user-id" data-rota-id="@rota.Id" />
                    <div class="voluntell-range-results" data-rota-id="@rota.Id"></div>
                </div>
                <div class="col-auto">
                    <button type="submit" class="btn btn-sm btn-success">Assign to Range</button>
                </div>
            </form>
        </div>
    }
}
```

- [ ] **Step 3: Add JavaScript for range voluntell search**

At the bottom of `src/Humans.Web/Views/ShiftAdmin/Index.cshtml`, in the existing `@section Scripts` block, add JS that handles the `.voluntell-range-search` inputs. Follow the exact same AJAX pattern as the existing single-shift volunteer search but target the range-specific CSS classes:

```javascript
document.querySelectorAll('.voluntell-range-search').forEach(input => {
    let debounceTimer;
    input.addEventListener('input', function () {
        const rotaId = this.dataset.rotaId;
        const query = this.value.trim();
        const resultsDiv = document.querySelector(`.voluntell-range-results[data-rota-id="${rotaId}"]`);
        const userIdInput = document.querySelector(`.voluntell-range-user-id[data-rota-id="${rotaId}"]`);

        clearTimeout(debounceTimer);
        if (query.length < 2) {
            resultsDiv.innerHTML = '';
            return;
        }

        debounceTimer = setTimeout(async () => {
            const response = await fetch(`${window.location.pathname}/SearchVolunteers?query=${encodeURIComponent(query)}`);
            const volunteers = await response.json();
            resultsDiv.innerHTML = volunteers.map(v =>
                `<div class="dropdown-item voluntell-range-pick" style="cursor:pointer"
                      data-user-id="${v.id}" data-rota-id="${rotaId}">
                    ${v.displayName}
                </div>`
            ).join('');
        }, 300);
    });
});

document.addEventListener('click', function (e) {
    if (e.target.classList.contains('voluntell-range-pick')) {
        const rotaId = e.target.dataset.rotaId;
        const userId = e.target.dataset.userId;
        const name = e.target.textContent.trim();
        document.querySelector(`.voluntell-range-user-id[data-rota-id="${rotaId}"]`).value = userId;
        document.querySelector(`.voluntell-range-search[data-rota-id="${rotaId}"]`).value = name;
        document.querySelector(`.voluntell-range-results[data-rota-id="${rotaId}"]`).innerHTML = '';
    }
});
```

Note: Review the existing volunteer search JS in the same file and match its exact patterns (API endpoint URL construction, response shape, error handling). The above is a starting point — adapt to match.

- [ ] **Step 4: Build and verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftAdminController.cs \
  src/Humans.Web/Views/ShiftAdmin/Index.cshtml
git commit -m "feat: add range voluntell UI for batch shift assignment (#176)"
```

---

## Task 5: LegalDocumentService — Interface + Implementation + Tests (#194)

**Files:**
- Create: `src/Humans.Application/Interfaces/ILegalDocumentService.cs`
- Create: `src/Humans.Infrastructure/Services/LegalDocumentService.cs`
- Create: `tests/Humans.Application.Tests/Services/LegalDocumentServiceTests.cs`
- Modify: `src/Humans.Application/CacheKeys.cs`
- Modify: `src/Humans.Web/Program.cs`

- [ ] **Step 1: Write failing test — GetAvailableDocuments returns document list**

Create `tests/Humans.Application.Tests/Services/LegalDocumentServiceTests.cs`:

```csharp
using Humans.Application.Interfaces;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public class LegalDocumentServiceTests
{
    private readonly ILegalDocumentService _service;

    public LegalDocumentServiceTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = Substitute.For<ILogger<LegalDocumentService>>();
        var settings = new GitHubSettings
        {
            Owner = "nobodies-collective",
            Repository = "legal"
        };
        _service = new LegalDocumentService(cache, logger, settings);
    }

    [Fact]
    public void GetAvailableDocuments_ReturnsStatutes()
    {
        var docs = _service.GetAvailableDocuments();

        docs.Should().NotBeEmpty();
        docs.Should().Contain(d => d.Slug == "statutes");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Humans.Application.Tests --filter "GetAvailableDocuments_ReturnsStatutes" -v n`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Create ILegalDocumentService interface**

Create `src/Humans.Application/Interfaces/ILegalDocumentService.cs`:

```csharp
namespace Humans.Application.Interfaces;

public record LegalDocumentDefinition(string Slug, string DisplayName, string RepoFolder, string FilePrefix);

public interface ILegalDocumentService
{
    IReadOnlyList<LegalDocumentDefinition> GetAvailableDocuments();
    Task<Dictionary<string, string>> GetDocumentContentAsync(string slug);
}
```

- [ ] **Step 4: Add cache key to CacheKeys.cs**

In `src/Humans.Application/CacheKeys.cs`, add:

```csharp
public static string LegalDocument(string slug) => $"Legal:{slug}";
```

- [ ] **Step 5: Create LegalDocumentService implementation**

Create `src/Humans.Infrastructure/Services/LegalDocumentService.cs`:

```csharp
using System.Text.RegularExpressions;
using Humans.Application;
using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Humans.Infrastructure.Services;

public class LegalDocumentService : ILegalDocumentService
{
    private static readonly LegalDocumentDefinition[] Documents =
    [
        new("statutes", "Statutes", "Estatutos", "ESTATUTOS"),
    ];

    private static readonly Regex LanguageFilePattern = new(
        @"^(?<name>.+?)(?:-(?<lang>[A-Za-z]{2}))?\.md$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly IMemoryCache _cache;
    private readonly ILogger<LegalDocumentService> _logger;
    private readonly GitHubSettings _gitHubSettings;

    public LegalDocumentService(
        IMemoryCache cache,
        ILogger<LegalDocumentService> logger,
        GitHubSettings gitHubSettings)
    {
        _cache = cache;
        _logger = logger;
        _gitHubSettings = gitHubSettings;
    }

    public IReadOnlyList<LegalDocumentDefinition> GetAvailableDocuments() => Documents;

    public async Task<Dictionary<string, string>> GetDocumentContentAsync(string slug)
    {
        var definition = Documents.FirstOrDefault(d =>
            string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            return await _cache.GetOrCreateAsync(
                CacheKeys.LegalDocument(slug),
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                    return await FetchDocumentContentAsync(definition);
                }) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch legal document {Slug} from GitHub", slug);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task<Dictionary<string, string>> FetchDocumentContentAsync(LegalDocumentDefinition definition)
    {
        var client = new GitHubClient(new ProductHeaderValue("NobodiesHumans"));
        if (!string.IsNullOrEmpty(_gitHubSettings.AccessToken))
        {
            client.Credentials = new Credentials(_gitHubSettings.AccessToken);
        }

        var files = await client.Repository.Content.GetAllContents(
            _gitHubSettings.Owner,
            _gitHubSettings.Repository,
            definition.RepoFolder);

        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Where(f =>
            f.Name.StartsWith(definition.FilePrefix, StringComparison.OrdinalIgnoreCase) &&
            f.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            var match = LanguageFilePattern.Match(file.Name);
            if (!match.Success) continue;

            var lang = match.Groups["lang"].Success
                ? match.Groups["lang"].Value.ToLowerInvariant()
                : "es";

            var fileContent = await client.Repository.Content.GetAllContents(
                _gitHubSettings.Owner,
                _gitHubSettings.Repository,
                file.Path);

            if (fileContent.Count > 0 && fileContent[0].Content is not null)
            {
                content[lang] = fileContent[0].Content;
            }
        }

        return content;
    }
}
```

- [ ] **Step 6: Register service in Program.cs**

In `src/Humans.Web/Program.cs`, find where other services are registered (search for `AddScoped<IShiftSignupService>` or similar) and add:

```csharp
builder.Services.AddScoped<ILegalDocumentService, LegalDocumentService>();
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/Humans.Application.Tests --filter "LegalDocumentService" -v n`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/Interfaces/ILegalDocumentService.cs \
  src/Humans.Infrastructure/Services/LegalDocumentService.cs \
  src/Humans.Application/CacheKeys.cs \
  src/Humans.Web/Program.cs \
  tests/Humans.Application.Tests/Services/LegalDocumentServiceTests.cs
git commit -m "feat: add LegalDocumentService with GitHub fetching and caching (#194)"
```

---

## Task 6: LegalController + View + Navigation (#194)

**Depends on:** Task 5

**Files:**
- Create: `src/Humans.Web/Controllers/LegalController.cs`
- Create: `src/Humans.Web/Models/LegalPageViewModel.cs`
- Create: `src/Humans.Web/Views/Legal/Index.cshtml`
- Modify: `src/Humans.Web/Authorization/MembershipRequiredFilter.cs`
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml`
- Modify: `src/Humans.Web/Views/Shared/_LoginPartial.cshtml`

- [ ] **Step 1: Create LegalPageViewModel**

Create `src/Humans.Web/Models/LegalPageViewModel.cs`:

```csharp
using Humans.Application.Interfaces;

namespace Humans.Web.Models;

public class LegalPageViewModel
{
    public required IReadOnlyList<LegalDocumentDefinition> AllDocuments { get; init; }
    public required string CurrentSlug { get; init; }
    public required string CurrentDocumentName { get; init; }
    public required TabbedMarkdownDocumentsViewModel DocumentContent { get; init; }
}
```

- [ ] **Step 2: Create LegalController**

Create `src/Humans.Web/Controllers/LegalController.cs`:

```csharp
using Humans.Application.Interfaces;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Route("Legal")]
[AllowAnonymous]
public class LegalController : Controller
{
    private readonly ILegalDocumentService _legalDocService;

    public LegalController(ILegalDocumentService legalDocService)
    {
        _legalDocService = legalDocService;
    }

    [HttpGet("{slug?}")]
    public async Task<IActionResult> Index(string? slug)
    {
        var documents = _legalDocService.GetAvailableDocuments();
        if (documents.Count == 0)
            return NotFound();

        var currentDoc = slug is not null
            ? documents.FirstOrDefault(d => string.Equals(d.Slug, slug, StringComparison.OrdinalIgnoreCase))
            : documents[0];

        if (currentDoc is null)
            return NotFound();

        var content = await _legalDocService.GetDocumentContentAsync(currentDoc.Slug);
        var orderedContent = content.OrderByDisplayLanguage(canonicalFirst: true).ToList();
        var defaultLang = content.GetDefaultDocumentLanguage();

        var viewModel = new LegalPageViewModel
        {
            AllDocuments = documents,
            CurrentSlug = currentDoc.Slug,
            CurrentDocumentName = currentDoc.DisplayName,
            DocumentContent = new TabbedMarkdownDocumentsViewModel
            {
                Documents = orderedContent,
                DefaultLanguage = defaultLang,
                TabsId = "legal-tabs",
                ContentId = "legal-tabs-content",
                ContentStyle = "",  // Full-page, no max-height
                EmptyMessage = "Document not yet available.",
                UseLegalLanguageLabels = true
            }
        };

        ViewData["Title"] = currentDoc.DisplayName;
        return View(viewModel);
    }
}
```

- [ ] **Step 3: Add "Legal" to MembershipRequiredFilter exempt list**

In `src/Humans.Web/Authorization/MembershipRequiredFilter.cs`, add `"Legal"` to the `ExemptControllers` set (around line 15-32).

- [ ] **Step 4: Create Legal/Index.cshtml view**

Create `src/Humans.Web/Views/Legal/Index.cshtml`:

```html
@model Humans.Web.Models.LegalPageViewModel

@{
    ViewData["Title"] = Model.CurrentDocumentName;
}

<h2>@Model.CurrentDocumentName</h2>

@if (Model.AllDocuments.Count > 1)
{
    <ul class="nav nav-pills mb-4">
        @foreach (var doc in Model.AllDocuments)
        {
            <li class="nav-item">
                <a class="nav-link @(doc.Slug == Model.CurrentSlug ? "active" : "")"
                   asp-action="Index" asp-route-slug="@doc.Slug">
                    @doc.DisplayName
                </a>
            </li>
        }
    </ul>
}

@await Html.PartialAsync("_TabbedMarkdownDocuments", Model.DocumentContent)
```

- [ ] **Step 5: Add "Legal" link to navbar for logged-out users**

In `src/Humans.Web/Views/Shared/_Layout.cshtml`, after the Teams nav item (around line 47) and before the `@if (User.Identity?.IsAuthenticated == true)` check (line 48), add:

```html
@if (User.Identity?.IsAuthenticated != true)
{
    <li class="nav-item">
        <a class="nav-link" asp-controller="Legal" asp-action="Index">Legal</a>
    </li>
}
```

- [ ] **Step 6: Add "Legal" link to profile dropdown for logged-in users**

In `src/Humans.Web/Views/Shared/_LoginPartial.cshtml`, after the Governance link (around line 58), add:

```html
<li>
    <a class="dropdown-item d-flex align-items-center gap-2" asp-controller="Legal" asp-action="Index">
        <i class="fa-solid fa-scale-balanced fa-fw"></i> Legal
    </a>
</li>
```

- [ ] **Step 7: Build and verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Web/Controllers/LegalController.cs \
  src/Humans.Web/Models/LegalPageViewModel.cs \
  src/Humans.Web/Views/Legal/Index.cshtml \
  src/Humans.Web/Authorization/MembershipRequiredFilter.cs \
  src/Humans.Web/Views/Shared/_Layout.cshtml \
  src/Humans.Web/Views/Shared/_LoginPartial.cshtml
git commit -m "feat: add public Legal section with pill nav and tabbed doc viewer (#194)"
```

---

## Task 7: Refactor GovernanceController to Use LegalDocumentService (#194)

**Depends on:** Task 5

**Files:**
- Modify: `src/Humans.Web/Controllers/GovernanceController.cs`

- [ ] **Step 1: Replace GitHub fetching with ILegalDocumentService injection**

In `src/Humans.Web/Controllers/GovernanceController.cs`:

1. Remove these fields/constants:
   - `private static readonly Regex LanguageFilePattern` (lines 33-36)
   - `private static readonly TimeSpan StatutesCacheTtl` (line 32)
   - `private readonly IMemoryCache _cache` field
   - `private readonly GitHubSettings _gitHubSettings` field

2. Add `ILegalDocumentService` to the constructor:
   ```csharp
   private readonly ILegalDocumentService _legalDocService;
   ```

3. Update constructor to inject `ILegalDocumentService` instead of `IMemoryCache` and `GitHubSettings`. Keep other existing dependencies (`IClock`, `ILogger`, etc.).

4. In the `Index` action, replace the call to `GetStatutesContentAsync()` with:
   ```csharp
   var statutesContent = await _legalDocService.GetDocumentContentAsync("statutes");
   ```

5. Delete private methods:
   - `GetStatutesContentAsync()` (lines 92-107)
   - `FetchStatutesContentAsync()` (lines 109-147)

6. Remove unused `using` statements (`Octokit`, `Microsoft.Extensions.Caching.Memory` if no longer needed).

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeds.

- [ ] **Step 3: Run full test suite**

Run: `dotnet test Humans.slnx`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/GovernanceController.cs
git commit -m "refactor: GovernanceController uses shared LegalDocumentService (#194)"
```

---

## Dependency Graph

```
Task 1 (#184 labels)          ─── independent
Task 2 (#180 rota separation) ─── independent (but touches same file as Task 1)
Task 3 (#176 service)         ─── independent
Task 4 (#176 UI)              ─── depends on Task 3
Task 5 (#194 service)         ─── independent
Task 6 (#194 controller+nav)  ─── depends on Task 5
Task 7 (#194 governance)      ─── depends on Task 5
```

**Parallel groups:**
- Group A: Tasks 1, 2 (shift views — overlap on Index.cshtml, merge carefully)
- Group B: Tasks 3 → 4 (voluntell, sequential)
- Group C: Tasks 5 → 6, 7 (legal, 6 and 7 can run in parallel after 5)
