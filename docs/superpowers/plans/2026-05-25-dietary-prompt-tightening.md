# Dietary Prompt Tightening Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tighten the existing dietary-medical nudge so the cantina never gets blindsided — soft prompt fires before any signup; hard gate blocks 6+ hour signups until dietary is filled; banner + disabled buttons catch humans who already have a qualifying signup.

**Architecture:** Pure addition on top of the existing dietary-medical-nudge work (issue #279). No new entities, no migrations. Three intervention points: (1) relax `ThingsToDoViewComponent`'s dietary-item gate, (2) add `RedirectIfDietaryMissingAsync` / `…ForRangeAsync` to `ShiftsController` and a one-time replay branch on `ProfileController.DietaryMedical` POST, (3) new `DietaryMissingBannerViewComponent` + a lockout flag on the rota-table view models.

**Tech Stack:** .NET / ASP.NET Core MVC + Razor, EF Core / NodaTime, xUnit + NSubstitute + AwesomeAssertions, six .resx locales (en/es/ca/de/fr/it).

**Spec:** `docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md`

**Branch:** `feature/dietary-prompt-tightening` (worktree at `.worktrees/dietary-prompt-tightening`, based on `fork/feature/issue-279-dietary-nudge-impl` at `f4a0c966`).

**Test placement:** new test files live under `tests/Humans.Application.Tests/Controllers/` and `tests/Humans.Application.Tests/ViewComponents/`. This matches existing precedent in the codebase — `ProfileControllerEditTests.cs`, `ProfileControllerPopoverTests.cs`, `AdminBreadcrumbViewComponentTests.cs` all live in `Humans.Application.Tests` despite testing Web-layer types. `tests/Humans.Web.Tests/` exists but is sparsely populated; do not invent a new convention.

**Chunk execution order:** chunks are presented in conceptual order (foundation → behavior → UI), but execution order is constrained: **Chunk 6.1 must land before Chunk 5.2**, because Chunk 5.2's banner invocation references `Model.UserId` on `ShiftBrowseViewModel` / `MyShiftsViewModel`, which Chunk 6.1 adds. Either execute 6.1 immediately after Chunk 5.1, or fold the `UserId` additions into Chunk 5.2's commit. All other chunks are order-independent within reason; the foundation chunk (1) must come first regardless.

**Build commands (from worktree root):**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

`-v quiet` is required per `memory/process/dotnet-verbosity-quiet.md`.

---

## File Structure

**Modify:**

- `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs` — add `PeekRangeShiftsAsync` method.
- `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` — implement `PeekRangeShiftsAsync` by lifting the range-enumeration logic from inside `SignUpRangeAsync`.
- `src/Humans.Web/ViewComponents/ThingsToDoViewComponent.cs` — relax the dietary-item gate; add no-shift copy branch.
- `src/Humans.Web/Controllers/ShiftsController.cs` — two new private helpers (`RedirectIfDietaryMissingAsync`, `RedirectIfDietaryMissingForRangeAsync`); call them from `SignUp` / `SignUpRange`; populate `SignupsBlockedByMissingDietary` on the two top-level view models.
- `src/Humans.Web/Controllers/ProfileController.cs` — inject `IShiftSignupService`; add `returnAction` branch handling to `DietaryMedical` POST.
- `src/Humans.Web/Views/Profile/DietaryMedical.cshtml` — add hidden fields for `returnAction`, `shiftId`, `rotaId`, `startDayOffset`, `endDayOffset` round-trip.
- `src/Humans.Web/Models/ShiftViewModels.cs` — add `SignupsBlockedByMissingDietary` to `ShiftBrowseViewModel`, `MyShiftsViewModel`, `EventRotaTableViewModel`, `BuildStrikeRotaTableViewModel`.
- `src/Humans.Web/Views/Shifts/Index.cshtml` — render banner; pass flag down to each rota-partial construction site.
- `src/Humans.Web/Views/Shifts/Mine.cshtml` — same.
- `src/Humans.Web/Views/Shared/_EventRotaTable.cshtml` — render disabled Sign-Up button when flag is true.
- `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml` — same.
- `src/Humans.Web/Resources/SharedResource.resx` + `.es.resx`, `.ca.resx`, `.de.resx`, `.fr.resx`, `.it.resx` — new keys.
- `docs/features/profiles/dietary-medical-nudge.md` — append US-35.5 + US-35.6 referencing this design.

**Create:**

- `src/Humans.Web/ViewComponents/DietaryMissingBannerViewComponent.cs`
- `src/Humans.Web/Views/Shared/Components/DietaryMissingBanner/Default.cshtml`
- `tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServicePeekRangeShiftsTests.cs`
- `tests/Humans.Application.Tests/ViewComponents/ThingsToDoViewComponentDietaryGateTests.cs` (if no existing test file for the component — verify and reuse if it exists)
- `tests/Humans.Application.Tests/ViewComponents/DietaryMissingBannerViewComponentTests.cs`
- `tests/Humans.Application.Tests/Controllers/ShiftsControllerDietaryGateTests.cs`
- `tests/Humans.Application.Tests/Controllers/ProfileControllerDietaryMedicalReplayTests.cs`

---

## Chunk 1: Foundation — resource keys + `PeekRangeShiftsAsync`

Lay the load-bearing primitives the rest of the plan depends on. Pure additions, no behavior change yet.

### Task 1.1: Add localized resource keys (six locales)

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.es.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.ca.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.de.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.fr.resx`
- Modify: `src/Humans.Web/Resources/SharedResource.it.resx`

**New keys (English):**

| Key | Value |
|---|---|
| `Todo_DietaryMedical_NoShift_Pending` | `Tell us about your food needs so we're ready when you sign up.` |
| `DietaryMissingBanner_Title` | `We need your dietary info` |
| `DietaryMissingBanner_Body` | `You're signed up for a long shift. The cantina can't plan your meals until you tell us your food preferences.` |
| `DietaryMissingBanner_Cta` | `Tell us now` |
| `Shifts_SignupDisabledTooltip_MissingDietary` | `Tell us your food needs first.` |
| `Shifts_DietaryRequiredBeforeSignup` | `Quick stop — we need your dietary info before signing up for this shift.` |

- [ ] **Step 1: Add the six keys to `SharedResource.resx`** with the English values above.
- [ ] **Step 2: Translate and add to `.es.resx`.** Use the same wording style as existing `Todo_DietaryMedical_*` keys. Spanish values:
  - `Todo_DietaryMedical_NoShift_Pending` → `Cuéntanos tus necesidades alimentarias para estar listos cuando te apuntes.`
  - `DietaryMissingBanner_Title` → `Necesitamos tus datos alimentarios`
  - `DietaryMissingBanner_Body` → `Estás apuntada a un turno largo. La cantina no puede planificar tus comidas hasta que nos digas tus preferencias.`
  - `DietaryMissingBanner_Cta` → `Decírnoslo ahora`
  - `Shifts_SignupDisabledTooltip_MissingDietary` → `Primero cuéntanos tus necesidades alimentarias.`
  - `Shifts_DietaryRequiredBeforeSignup` → `Un momento — necesitamos tu información alimentaria antes de apuntarte a este turno.`
- [ ] **Step 3: Add to `.ca.resx`, `.de.resx`, `.fr.resx`, `.it.resx`** — use the English value as the placeholder if a quick translation isn't obvious; the existing `Todo_DietaryMedical_Pending` translations in those files show whether they were translated or left as English (mirror that pattern).
- [ ] **Step 4: Build**

```bash
dotnet build Humans.slnx -v quiet
```
Expected: 0 warnings, 0 errors. (Resource generator picks up the new keys; no consumers yet, so this just validates the .resx XML.)

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Resources/SharedResource*.resx
git commit -m "feat(resources): localized strings for dietary prompt tightening

Adds Todo_DietaryMedical_NoShift_Pending, DietaryMissingBanner_*,
Shifts_SignupDisabledTooltip_MissingDietary, and
Shifts_DietaryRequiredBeforeSignup across en/es/ca/de/fr/it.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 1.2: Add `IShiftSignupService.PeekRangeShiftsAsync`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs`
- Modify: `src/Humans.Application/Services/Shifts/ShiftSignupService.cs`
- Create: `tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServicePeekRangeShiftsTests.cs`

**Context:** `SignUpRangeAsync` (around `ShiftSignupService.cs:546`) already loads the rota via `_repo.GetRotaWithShiftsAsync(rotaId)` and filters to all-day shifts within `[startDayOffset, endDayOffset]`. We're lifting that filter into a separate method so the new dietary gate can reuse it without going through the signup write path.

Verified primitives on the baseline:
- `IShiftSignupRepository.GetRotaWithShiftsAsync(Guid rotaId, CancellationToken ct = default)` — `src/Humans.Application/Interfaces/Repositories/IShiftSignupRepository.cs:169`.
- `_repo` in `ShiftSignupService` is `IShiftSignupRepository`.
- `Rota.Shifts` is `{ get; } = new List<Shift>()` (init-only collection, mutate via `.Add`).

- [ ] **Step 1: Open `ShiftSignupService.cs` around line 546** to confirm the exact filter shape used inside `SignUpRangeAsync`. Note inclusivity of `startDayOffset` / `endDayOffset` — the new helper must match it byte-for-byte.

- [ ] **Step 2: Add the interface method** to `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs`:

```csharp
/// <summary>
/// Returns the set of all-day shifts within the given offset range on the rota.
/// Used by callers that need to peek the candidate set before calling
/// <see cref="SignUpRangeAsync"/> — e.g. the dietary-prompt gate that needs
/// to know whether any shift in the range qualifies for cantina meals.
/// </summary>
Task<IReadOnlyList<Shift>> PeekRangeShiftsAsync(
    Guid rotaId,
    int startDayOffset,
    int endDayOffset,
    CancellationToken ct = default);
```

- [ ] **Step 3: Write the failing test.** Create `tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServicePeekRangeShiftsTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Application.Services.Shifts;
using Humans.Domain.Entities;
using NSubstitute;
using Xunit;
// + existing using block for repository interfaces, NodaTime testing, etc. — mirror existing ShiftSignupServiceTests.cs
namespace Humans.Application.Tests.Services.Shifts;

public class ShiftSignupServicePeekRangeShiftsTests
{
    [Fact]
    public async Task ReturnsAllDayShiftsInOffsetWindow()
    {
        // Arrange: rota with 5 shifts — 3 all-day at offsets 0/1/2, 1 all-day at offset 5, 1 non-all-day at offset 1.
        var rotaId = Guid.NewGuid();
        var rota = new Rota { Id = rotaId };
        rota.Shifts.Add(BuildShift(dayOffset: 0, isAllDay: true));
        rota.Shifts.Add(BuildShift(dayOffset: 1, isAllDay: true));
        rota.Shifts.Add(BuildShift(dayOffset: 1, isAllDay: false)); // excluded by IsAllDay filter
        rota.Shifts.Add(BuildShift(dayOffset: 2, isAllDay: true));
        rota.Shifts.Add(BuildShift(dayOffset: 5, isAllDay: true));  // excluded by offset window
        _repo.GetRotaWithShiftsAsync(rotaId, Arg.Any<CancellationToken>())
             .Returns(rota);

        // Act
        var result = await _sut.PeekRangeShiftsAsync(rotaId, startDayOffset: 0, endDayOffset: 2);

        // Assert: 3 all-day shifts at offsets 0, 1, 2.
        result.Should().HaveCount(3);
        result.Select(s => s.DayOffset).Should().BeEquivalentTo(new[] { 0, 1, 2 });
    }

    [Fact]
    public async Task ReturnsEmptyWhenNoShiftsInWindow()
    {
        var rotaId = Guid.NewGuid();
        var rota = new Rota { Id = rotaId };
        rota.Shifts.Add(BuildShift(dayOffset: 10, isAllDay: true));
        _repo.GetRotaWithShiftsAsync(rotaId, Arg.Any<CancellationToken>())
             .Returns(rota);

        var result = await _sut.PeekRangeShiftsAsync(rotaId, startDayOffset: 0, endDayOffset: 2);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnsEmptyWhenRotaNotFound()
    {
        var rotaId = Guid.NewGuid();
        _repo.GetRotaWithShiftsAsync(rotaId, Arg.Any<CancellationToken>())
             .Returns((Rota?)null);

        var result = await _sut.PeekRangeShiftsAsync(rotaId, startDayOffset: 0, endDayOffset: 2);

        result.Should().BeEmpty();
    }

    // Helpers — mirror ShiftSignupServiceTests.cs for ShiftSignupService construction with NSubstitute mocks.
    // _repo and _sut are class fields initialized in the constructor (see sibling test for the full DI list).
    private readonly IShiftSignupRepository _repo = Substitute.For<IShiftSignupRepository>();
    private readonly ShiftSignupService _sut; // constructor wires _repo + other NSubstitute mocks for deps not under test
    private static Shift BuildShift(int dayOffset, bool isAllDay) =>
        new() { Id = Guid.NewGuid(), DayOffset = dayOffset, IsAllDay = isAllDay };
}
```

Mirror the existing `ShiftSignupServiceTests.cs` for the `BuildService` helper — it has the construction wiring. Don't write that wiring from scratch; read the sibling file and follow its pattern.

- [ ] **Step 4: Run to verify it fails**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ShiftSignupServicePeekRangeShiftsTests"
```
Expected: build error or test failure (method doesn't exist yet).

- [ ] **Step 5: Implement `PeekRangeShiftsAsync`** in `ShiftSignupService.cs`:

```csharp
public async Task<IReadOnlyList<Shift>> PeekRangeShiftsAsync(
    Guid rotaId,
    int startDayOffset,
    int endDayOffset,
    CancellationToken ct = default)
{
    var rota = await _repo.GetRotaWithShiftsAsync(rotaId, ct);
    if (rota is null) return Array.Empty<Shift>();

    return rota.Shifts
        .Where(s => s.IsAllDay
                    && s.DayOffset >= startDayOffset
                    && s.DayOffset <= endDayOffset)
        .ToList();
}
```

The bounds match the existing `SignUpRangeAsync` filter at `ShiftSignupService.cs:546`. If a future reader edits `SignUpRangeAsync`'s filter without updating this method (or vice versa), the integration paths diverge — pin them together via Step 7 (refactor `SignUpRangeAsync` to use the new method) where safe.

- [ ] **Step 6: Run tests to verify they pass**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ShiftSignupServicePeekRangeShiftsTests"
```
Expected: 3 passing.

- [ ] **Step 7: Optional but recommended — refactor `SignUpRangeAsync` to use the new method.** If the lift is non-invasive (same filter, no behavior change), replace the inline filter inside `SignUpRangeAsync` with a call to `PeekRangeShiftsAsync`. Run the full Shifts test suite to confirm no regression:

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ShiftSignup"
```
Expected: all pre-existing tests still pass. If the refactor would change ordering/eager-loading observably (e.g., the inline filter passes an entity that's already loaded vs. our method re-fetches), skip the refactor and leave a `// TODO: lift filter into PeekRangeShiftsAsync` comment.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs \
        src/Humans.Application/Services/Shifts/ShiftSignupService.cs \
        tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServicePeekRangeShiftsTests.cs
git commit -m "feat(shifts): IShiftSignupService.PeekRangeShiftsAsync

Lifts the all-day-and-in-offset-window filter from SignUpRangeAsync into
a reusable read method so the dietary-prompt gate can peek the candidate
set without going through the signup write path.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 2: Relax `ThingsToDoViewComponent` dietary-item gate

Move the dietary-item gate from "has qualifying signup" to "dietary empty (any reason)" and add the no-shift copy variant.

### Task 2.1: Relax the gate and add no-shift copy

**Files:**
- Modify: `src/Humans.Web/ViewComponents/ThingsToDoViewComponent.cs`
- Create or modify: `tests/Humans.Application.Tests/ViewComponents/ThingsToDoViewComponentDietaryGateTests.cs` (check if a `ThingsToDoViewComponentTests.cs` already exists in that directory; if so, add to it instead)

**Behavior change:** the existing `if (hasQualifyingSignup)` wrapping the dietary item (around `ThingsToDoViewComponent.cs:145`) is removed. The item is always added when `dietaryEmpty`, with two copy branches:

| `hasQualifyingSignup` | `Description` |
|---|---|
| `true` | `Todo_DietaryMedical_Pending` (existing) |
| `false` | `Todo_DietaryMedical_NoShift_Pending` (new in Chunk 1) |

- [ ] **Step 1: Verify if a test file for `ThingsToDoViewComponent` already exists**:

```bash
find tests -name "ThingsToDoViewComponent*"
```
If found, add new cases to it. If not, create the new file.

- [ ] **Step 2: Write the failing tests** (in a new or existing test file):

```csharp
[Fact]
public async Task DietaryItemAppearsWithNoShiftCopyWhenNoQualifyingSignup()
{
    // Arrange: profile with empty dietary; service reports no qualifying signup.
    var userId = Guid.NewGuid();
    _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(false);
    _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false)
              .Returns(new VolunteerEventProfile { DietaryPreference = null });
    // (existing setup for other items can be left to defaults)

    // Act
    var result = await _sut.InvokeAsync(userId, isVolunteerMember: true, hasShiftSignups: false, profileCompletionPercent: 100);

    // Assert: dietary-medical item present, uses _NoShift_Pending resource key.
    var model = (ThingsToDoViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
    var dietary = model.Items.Should().ContainSingle(i => i.Key == "dietary-medical").Subject;
    dietary.IsDone.Should().BeFalse();
    dietary.Description.Should().Be(_localizer["Todo_DietaryMedical_NoShift_Pending"].Value);
}

[Fact]
public async Task DietaryItemUsesExistingCopyWhenHasQualifyingSignup()
{
    var userId = Guid.NewGuid();
    _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(true);
    _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false)
              .Returns(new VolunteerEventProfile { DietaryPreference = null });

    var result = await _sut.InvokeAsync(userId, isVolunteerMember: true, hasShiftSignups: true, profileCompletionPercent: 100);

    var model = (ThingsToDoViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
    var dietary = model.Items.Should().ContainSingle(i => i.Key == "dietary-medical").Subject;
    dietary.Description.Should().Be(_localizer["Todo_DietaryMedical_Pending"].Value);
}

[Fact]
public async Task DietaryItemNotAddedWhenDietaryFilled()
{
    var userId = Guid.NewGuid();
    _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(false);
    _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false)
              .Returns(new VolunteerEventProfile { DietaryPreference = "Vegetarian" });

    var result = await _sut.InvokeAsync(userId, isVolunteerMember: true, hasShiftSignups: false, profileCompletionPercent: 100);

    if (result is ViewViewComponentResult viewResult)
    {
        var model = (ThingsToDoViewModel?)viewResult.ViewData!.Model;
        model?.Items.Should().NotContain(i => i.Key == "dietary-medical");
    }
    // ContentResult (empty) is also valid — means the card hid entirely because all items are Done.
}
```

- [ ] **Step 3: Run to verify failure**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ThingsToDoViewComponent"
```
Expected: the two new "NoShift" tests fail (current gate requires `hasQualifyingSignup`); the existing-copy test should pass.

- [ ] **Step 4: Update `ThingsToDoViewComponent.cs`**. Replace the existing dietary block (around lines 138–165) with:

```csharp
// 5. Dietary & medical nudge — fires whenever DietaryPreference is empty.
// Copy varies by whether the user has an active qualifying signup; the
// item is the same Key either way so it disappears with the rest of the
// card when DietaryPreference becomes non-empty.
// See docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md
try
{
    var dietaryProfile = await _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false);
    var dietaryEmpty = string.IsNullOrEmpty(dietaryProfile?.DietaryPreference);
    if (dietaryEmpty)
    {
        var hasQualifyingSignup = await _shiftMgmt.HasQualifyingCantinaSignupAsync(userId);
        var descriptionKey = hasQualifyingSignup
            ? "Todo_DietaryMedical_Pending"
            : "Todo_DietaryMedical_NoShift_Pending";
        model.Items.Add(new TodoItem
        {
            Key = "dietary-medical",
            Title = _localizer["Todo_DietaryMedical_Title"].Value,
            Description = _localizer[descriptionKey].Value,
            IsDone = false,
            ActionUrl = Url.Action("DietaryMedical", "Profile"),
            ActionText = _localizer["Todo_DietaryMedical_Action"].Value,
            IconClass = "fa-solid fa-utensils",
        });
    }
}
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to check dietary/medical nudge for user {UserId}", userId);
}
```

Note: the `IsDone == true` branch is dropped — when dietary is filled the item is omitted entirely (matches the "card hides when all items done" pattern in the same component). If preserving the "Done" tick is important for UX, restore that branch; otherwise omit it (YAGNI).

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ThingsToDoViewComponent"
```
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/ViewComponents/ThingsToDoViewComponent.cs \
        tests/Humans.Application.Tests/ViewComponents/ThingsToDoViewComponent*Tests.cs
git commit -m "feat(dashboard): dietary nudge fires before any signup exists

ThingsToDoViewComponent's dietary item now appears whenever
DietaryPreference is empty, using _NoShift_Pending copy when the
user has no qualifying signup yet. Existing copy is preserved for
users with a qualifying signup.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 3: ShiftsController hard gate — `RedirectIfDietaryMissingAsync` + `…ForRangeAsync`

Add two private helpers and wire them into `SignUp` / `SignUpRange`.

### Task 3.1: Single-shift redirect helper + tests

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`
- Create: `tests/Humans.Application.Tests/Controllers/ShiftsControllerDietaryGateTests.cs`

**Behavior:** before delegating to `_signupService.SignUpAsync`, check whether the target shift `QualifiesForCantinaMeal()` and the user's `DietaryPreference` is empty. If both true, set an info flash and redirect to `ProfileController.DietaryMedical` with `returnAction=signup&shiftId={id}`.

**Verified primitives on the baseline:**
- `IShiftManagementService.GetShiftByIdAsync(Guid shiftId)` — **no `CancellationToken`** in the signature (`src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs:165`).
- `HumansControllerBase.ResolveCurrentUserOrChallengeAsync()` returns `(IActionResult? ErrorResult, User User)` — the helper lives on the base controller, not on `IUserService`. Param type is `Humans.Domain.Entities.User`, NOT `UserInfo` (which doesn't exist in this codebase).
- `IShiftSignupService.SignUpAsync(Guid userId, Guid shiftId, Guid? actorUserId = null, bool isPrivileged = false)` — 4 params, not 3.
- Tests mock `_signupService` (an `IShiftSignupService`) and `_shiftMgmt` (an `IShiftManagementService`); the base-controller helper's user resolution is harder to mock — mirror `ProfileControllerEditTests.cs` for the established pattern (likely a real `User` injected via a stub `UserManager<User>`, since `HumansControllerBase` takes a `UserManager<User>`).

- [ ] **Step 1: Read `ProfileControllerEditTests.cs`** to confirm the test-construction pattern: how `UserManager<User>` is stubbed so `HumansControllerBase.ResolveCurrentUserOrChallengeAsync()` returns the expected user. Mirror that pattern verbatim — do NOT invent a new mocking strategy.

- [ ] **Step 2: Write the failing tests** for the helper's branches. The test class includes a `BuildController` helper that wires `ShiftsController` with NSubstitute mocks for `IShiftSignupService`, `IShiftManagementService`, etc., plus a stub `UserManager<User>` returning the seeded user.

```csharp
// tests/Humans.Application.Tests/Controllers/ShiftsControllerDietaryGateTests.cs
public class ShiftsControllerDietaryGateTests
{
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    // ... other deps, mirrored from ProfileControllerEditTests.cs
    private readonly User _user;
    private readonly ShiftsController _controller;

    public ShiftsControllerDietaryGateTests()
    {
        _user = new User { Id = Guid.NewGuid() };
        _controller = BuildController(); // see ProfileControllerEditTests for the wiring shape
    }

    [Fact]
    public async Task SignUp_DietaryEmpty_QualifyingShift_RedirectsToDietaryMedical()
    {
        var shiftId = Guid.NewGuid();
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: true));
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { DietaryPreference = null });

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("DietaryMedical");
        redirect.ControllerName.Should().Be("Profile");
        redirect.RouteValues!["returnAction"].Should().Be("signup");
        redirect.RouteValues["shiftId"].Should().Be(shiftId);
        await _signupService.DidNotReceive()
            .SignUpAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task SignUp_DietaryEmpty_NonQualifyingShift_ProceedsToSignup()
    {
        var shiftId = Guid.NewGuid();
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: false));
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { DietaryPreference = null });
        _signupService.SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>())
                      .Returns(new SignupResult { Success = true });

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        await _signupService.Received(1)
            .SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>());
        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be(nameof(ShiftsController.Index));
    }

    [Fact]
    public async Task SignUp_DietaryFilled_QualifyingShift_ProceedsToSignup()
    {
        var shiftId = Guid.NewGuid();
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: true));
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { DietaryPreference = "Vegan" });
        _signupService.SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>())
                      .Returns(new SignupResult { Success = true });

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        await _signupService.Received(1)
            .SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task SignUp_DietaryEmpty_QualifyingShift_PrivilegedActor_StillRedirects()
    {
        // The actor IS the user being signed up in ShiftsController.SignUp.
        // Privileged-actor only relaxes signup validation; it does not bypass the dietary gate.
        var shiftId = Guid.NewGuid();
        SetPrincipalAsPrivilegedSignupApprover(); // stub ClaimsPrincipal so IsPrivilegedSignupApprover returns true
        _shiftMgmt.GetShiftByIdAsync(shiftId).Returns(BuildShift(shiftId, qualifiesForCantina: true));
        _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
                  .Returns(new VolunteerEventProfile { DietaryPreference = null });

        var result = await _controller.SignUp(shiftId, null, null, null, null, null, null, null);

        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be("DietaryMedical");
        await _signupService.DidNotReceive()
            .SignUpAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>());
    }

    // Helpers
    private static Shift BuildShift(Guid id, bool qualifiesForCantina) =>
        // All-day shifts qualify; non-all-day shifts whose Duration ≥ 6h also qualify.
        // Simplest: toggle IsAllDay.
        new() { Id = id, IsAllDay = qualifiesForCantina };
}
```

If `ShiftsController` resolves the shift via a different path than `_shiftMgmt.GetShiftByIdAsync` once the helper is implemented in Step 4, update the mock target. The signature on `IShiftManagementService` is `GetShiftByIdAsync(Guid shiftId)` — no `CancellationToken`.

- [ ] **Step 3: Run to verify failure**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ShiftsControllerDietaryGateTests"
```
Expected: build error (helper doesn't exist yet) or test failures.

- [ ] **Step 4: Add the helper** to `ShiftsController.cs`. Place it next to the other private helpers (search for `private async Task<IActionResult?>` for the right block).

```csharp
private async Task<IActionResult?> RedirectIfDietaryMissingAsync(User user, Guid shiftId)
{
    var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
    if (shift is null || !shift.QualifiesForCantinaMeal()) return null;

    var profile = await _shiftMgmt.GetShiftProfileAsync(user.Id, includeMedical: false);
    if (!string.IsNullOrEmpty(profile?.DietaryPreference)) return null;

    SetInfo(_localizer["Shifts_DietaryRequiredBeforeSignup"].Value);
    return RedirectToAction(
        actionName: "DietaryMedical",
        controllerName: "Profile",
        routeValues: new { returnAction = "signup", shiftId });
}
```

Note: `IShiftManagementService.GetShiftByIdAsync` does not take a `CancellationToken` (verified at `src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs:165`); don't pass one.

- [ ] **Step 5: Wire the helper into `SignUp`** (around line 238 of the post-#279 branch). After `ResolveCurrentUserOrChallengeAsync`, before the `_signupService.SignUpAsync` call:

```csharp
if (await RedirectIfDietaryMissingAsync(user, shiftId) is { } gate)
    return gate;
```

- [ ] **Step 6: Run tests** — expect pass for all four new cases.

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ShiftsControllerDietaryGateTests"
```

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftsController.cs \
        tests/Humans.Application.Tests/Controllers/ShiftsControllerDietaryGateTests.cs
git commit -m "feat(shifts): RedirectIfDietaryMissingAsync gate on SignUp

Before delegating to ShiftSignupService.SignUpAsync, SignUp now checks
whether the target shift qualifies for cantina meals and the user's
DietaryPreference is empty. If both, redirects to the DietaryMedical
form with returnAction=signup so the form-completion handler can replay
the signup.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 3.2: Range redirect helper + tests + wire into `SignUpRange`

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`
- Modify: `tests/Humans.Application.Tests/Controllers/ShiftsControllerDietaryGateTests.cs`

- [ ] **Step 1: Write the failing tests**

`IShiftSignupService.SignUpRangeAsync` signature: `(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid? actorUserId = null, bool isPrivileged = false, bool skipConflicts = false)` — **7 params**. Tests use positional matchers consistent with that.

```csharp
[Fact]
public async Task SignUpRange_DietaryEmpty_RangeHasQualifyingShift_RedirectsToDietaryMedical()
{
    var rotaId = Guid.NewGuid();
    _signupService.PeekRangeShiftsAsync(rotaId, 0, 2, Arg.Any<CancellationToken>())
                  .Returns(new[] { BuildShift(Guid.NewGuid(), qualifiesForCantina: true) });
    _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
              .Returns(new VolunteerEventProfile { DietaryPreference = null });

    var result = await _controller.SignUpRange(rotaId, 0, 2, null, null, null, null, null, null, null);

    var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
    redirect.RouteValues!["returnAction"].Should().Be("signuprange");
    redirect.RouteValues["rotaId"].Should().Be(rotaId);
    redirect.RouteValues["startDayOffset"].Should().Be(0);
    redirect.RouteValues["endDayOffset"].Should().Be(2);
    await _signupService.DidNotReceive().SignUpRangeAsync(
        Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
        Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<bool>());
}

[Fact]
public async Task SignUpRange_DietaryEmpty_RangeEmpty_ProceedsToSignup()
{
    var rotaId = Guid.NewGuid();
    _signupService.PeekRangeShiftsAsync(rotaId, 0, 2, Arg.Any<CancellationToken>())
                  .Returns(Array.Empty<Shift>());
    _shiftMgmt.GetShiftProfileAsync(_user.Id, includeMedical: false)
              .Returns(new VolunteerEventProfile { DietaryPreference = null });
    _signupService.SignUpRangeAsync(_user.Id, rotaId, 0, 2, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<bool>())
                  .Returns(new SignupResult { Success = true });

    var result = await _controller.SignUpRange(rotaId, 0, 2, null, null, null, null, null, null, null);

    await _signupService.Received(1)
        .SignUpRangeAsync(_user.Id, rotaId, 0, 2, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<bool>());
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ShiftsControllerDietaryGateTests"
```

- [ ] **Step 3: Add the range helper** to `ShiftsController.cs`, next to the single-shift one:

```csharp
private async Task<IActionResult?> RedirectIfDietaryMissingForRangeAsync(
    User user, Guid rotaId, int startDayOffset, int endDayOffset)
{
    // Range candidates are all-day-only after the existing PeekRangeShiftsAsync filter,
    // and every all-day shift QualifiesForCantinaMeal(). So "any shift qualifies" reduces
    // to "filtered list is non-empty".
    var rangeShifts = await _signupService.PeekRangeShiftsAsync(rotaId, startDayOffset, endDayOffset, HttpContext.RequestAborted);
    if (rangeShifts.Count == 0) return null;

    var profile = await _shiftMgmt.GetShiftProfileAsync(user.Id, includeMedical: false);
    if (!string.IsNullOrEmpty(profile?.DietaryPreference)) return null;

    SetInfo(_localizer["Shifts_DietaryRequiredBeforeSignup"].Value);
    return RedirectToAction(
        actionName: "DietaryMedical",
        controllerName: "Profile",
        routeValues: new { returnAction = "signuprange", rotaId, startDayOffset, endDayOffset });
}
```

- [ ] **Step 4: Wire into `SignUpRange`** — after `ResolveCurrentUserOrChallengeAsync`, before the `_signupService.SignUpRangeAsync` call:

```csharp
if (await RedirectIfDietaryMissingForRangeAsync(user, rotaId, startDayOffset, endDayOffset) is { } gate)
    return gate;
```

- [ ] **Step 5: Run tests** — expect pass.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftsController.cs \
        tests/Humans.Application.Tests/Controllers/ShiftsControllerDietaryGateTests.cs
git commit -m "feat(shifts): RedirectIfDietaryMissingForRangeAsync gate on SignUpRange

Uses the new IShiftSignupService.PeekRangeShiftsAsync to determine
whether the range contains any all-day shift (which by definition
qualifies for cantina meals). Redirects with returnAction=signuprange
plus rotaId/startDayOffset/endDayOffset so the form-completion handler
can replay the range signup.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 4: ProfileController.DietaryMedical replay

Inject `IShiftSignupService`, branch on `returnAction` after a successful save, and round-trip the carryover query params via hidden fields.

### Task 4.1: Inject `IShiftSignupService` + add branch handling

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileController.cs`
- Modify: `src/Humans.Web/Models/DietaryMedicalViewModel.cs` (add `ReturnAction`, `ShiftId`, `RotaId`, `StartDayOffset`, `EndDayOffset` properties)
- Modify: `src/Humans.Web/Views/Profile/DietaryMedical.cshtml` (render hidden fields)
- Create: `tests/Humans.Application.Tests/Controllers/ProfileControllerDietaryMedicalReplayTests.cs`

- [ ] **Step 1: Extend `DietaryMedicalViewModel.cs`** with carryover properties:

```csharp
// Carryover from the redirect-then-replay flow (see dietary-prompt-tightening design).
// Not bound to VolunteerEventProfile — pure round-trip routing data.
public string? ReturnAction { get; set; }
public Guid? ShiftId { get; set; }
public Guid? RotaId { get; set; }
public int? StartDayOffset { get; set; }
public int? EndDayOffset { get; set; }
```

`ApplyTo(profile)` does NOT touch these — keep them outside the field-mapping logic.

- [ ] **Step 2: Update the GET action** (`ProfileController.DietaryMedical()` GET) to bind from query string. The default model binder handles `[FromQuery]` automatically for action method parameters, but since the GET already returns a `DietaryMedicalViewModel` built via `FromProfile(profile)`, we need to also copy any incoming query values:

```csharp
[HttpGet("Me/DietaryMedical")]
public async Task<IActionResult> DietaryMedical(
    string? returnAction = null,
    Guid? shiftId = null,
    Guid? rotaId = null,
    int? startDayOffset = null,
    int? endDayOffset = null)
{
    try
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return NotFound();

        var profile = await _shiftMgmt.GetShiftProfileAsync(user.Id, includeMedical: false);
        var model = profile is null
            ? new DietaryMedicalViewModel()
            : DietaryMedicalViewModel.FromProfile(profile);

        model.ReturnAction = returnAction;
        model.ShiftId = shiftId;
        model.RotaId = rotaId;
        model.StartDayOffset = startDayOffset;
        model.EndDayOffset = endDayOffset;

        return View(model);
    }
    catch (Exception ex)
    {
        // existing catch
    }
}
```

- [ ] **Step 3: Update `DietaryMedical.cshtml`** to render the carryover values as hidden fields inside the form. After the existing form-tag open, add:

```html
<input type="hidden" asp-for="ReturnAction" />
<input type="hidden" asp-for="ShiftId" />
<input type="hidden" asp-for="RotaId" />
<input type="hidden" asp-for="StartDayOffset" />
<input type="hidden" asp-for="EndDayOffset" />
```

Place them at the top of the form so they're tied to a submit regardless of validation state.

- [ ] **Step 4: Inject `IShiftSignupService`** into `ProfileController`. Find the constructor — add the parameter and assign to a private readonly field `_signupService`. Update the DI registration if `ProfileController` is constructed manually anywhere (search for `new ProfileController(`).

- [ ] **Step 5: Write the failing tests** in `ProfileControllerDietaryMedicalReplayTests.cs`. `IShiftSignupService.SignUpAsync` signature is `(Guid userId, Guid shiftId, Guid? actorUserId = null, bool isPrivileged = false)` — 4 params; mocks below use 4-arg matchers.

User resolution: `ProfileController.GetCurrentUserAsync()` is inherited from `HumansControllerBase`, which uses `UserManager<User>`. Mirror `ProfileControllerEditTests.cs` for how the user is stubbed — typically via a `UserManager<User>` stub that returns `_user` for the test principal. `_user` and `_controller` are constructor-initialized fields.

```csharp
public class ProfileControllerDietaryMedicalReplayTests
{
    private readonly IShiftSignupService _signupService = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagementService _shiftMgmt = Substitute.For<IShiftManagementService>();
    // ... other deps, mirrored from ProfileControllerEditTests.cs (IStringLocalizer<SharedResource>, etc.)
    private readonly User _user;
    private readonly ProfileController _controller;

    public ProfileControllerDietaryMedicalReplayTests()
    {
        _user = new User { Id = Guid.NewGuid() };
        _controller = BuildController(); // see ProfileControllerEditTests.cs constructor for wiring
    }

    [Fact]
    public async Task Post_ValidSave_ReturnActionSignup_CallsSignupService()
    {
        var shiftId = Guid.NewGuid();
        var model = new DietaryMedicalViewModel
        {
            DietaryPreference = "Vegan",
            Allergies = new(),
            Intolerances = new(),
            ReturnAction = "signup",
            ShiftId = shiftId,
        };
        _shiftMgmt.GetOrCreateShiftProfileAsync(_user.Id).Returns(new VolunteerEventProfile());
        _signupService.SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>())
                      .Returns(new SignupResult { Success = true });

        var result = await _controller.DietaryMedical(model);

        await _signupService.Received(1)
            .SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>());
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("Shifts");
    }

    [Fact]
    public async Task Post_ValidSave_ReturnActionSignupRange_CallsRangeSignup()
    {
        var rotaId = Guid.NewGuid();
        var model = new DietaryMedicalViewModel
        {
            DietaryPreference = "Vegan",
            Allergies = new(),
            Intolerances = new(),
            ReturnAction = "signuprange",
            RotaId = rotaId,
            StartDayOffset = 0,
            EndDayOffset = 3,
        };
        _shiftMgmt.GetOrCreateShiftProfileAsync(_user.Id).Returns(new VolunteerEventProfile());
        _signupService.SignUpRangeAsync(_user.Id, rotaId, 0, 3, Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<bool>())
                      .Returns(new SignupResult { Success = true });

        var result = await _controller.DietaryMedical(model);

        await _signupService.Received(1)
            .SignUpRangeAsync(_user.Id, rotaId, 0, 3, Arg.Any<Guid?>(), Arg.Any<bool>(), skipConflicts: true);
        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ControllerName.Should().Be("Shifts");
    }

    [Fact]
    public async Task Post_ValidSave_ReturnActionShifts_RedirectsToShifts_NoSignupReplay()
    {
        var model = new DietaryMedicalViewModel
        {
            DietaryPreference = "Vegan",
            Allergies = new(),
            Intolerances = new(),
            ReturnAction = "shifts",
        };
        _shiftMgmt.GetOrCreateShiftProfileAsync(_user.Id).Returns(new VolunteerEventProfile());

        var result = await _controller.DietaryMedical(model);

        await _signupService.DidNotReceive()
            .SignUpAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>());
        await _signupService.DidNotReceive().SignUpRangeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<bool>());
        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be("Index");
    }

    [Fact]
    public async Task Post_ValidSave_NoReturnAction_RedirectsToHome()
    {
        var model = new DietaryMedicalViewModel
        {
            DietaryPreference = "Vegan",
            Allergies = new(),
            Intolerances = new(),
        };
        _shiftMgmt.GetOrCreateShiftProfileAsync(_user.Id).Returns(new VolunteerEventProfile());

        var result = await _controller.DietaryMedical(model);

        await _signupService.DidNotReceive()
            .SignUpAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>());
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        redirect.ControllerName.Should().Be("Home");
    }

    [Fact]
    public async Task Post_ValidationFails_DoesNotReplay()
    {
        var model = new DietaryMedicalViewModel
        {
            // DietaryPreference deliberately empty — required validation fails
            ReturnAction = "signup",
            ShiftId = Guid.NewGuid(),
        };
        _controller.ModelState.AddModelError(nameof(model.DietaryPreference), "Required");

        var result = await _controller.DietaryMedical(model);

        await _signupService.DidNotReceive()
            .SignUpAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>());
        result.Should().BeOfType<ViewResult>();
    }

    [Fact]
    public async Task Post_SignupReplay_Fails_StillRedirectsToShifts_DietarySavePersisted()
    {
        var shiftId = Guid.NewGuid();
        var model = new DietaryMedicalViewModel
        {
            DietaryPreference = "Vegan",
            Allergies = new(),
            Intolerances = new(),
            ReturnAction = "signup",
            ShiftId = shiftId,
        };
        _shiftMgmt.GetOrCreateShiftProfileAsync(_user.Id).Returns(new VolunteerEventProfile());
        _signupService.SignUpAsync(_user.Id, shiftId, Arg.Any<Guid?>(), Arg.Any<bool>())
                      .Returns(new SignupResult { Success = false, Error = "Shift full" });

        var result = await _controller.DietaryMedical(model);

        // Dietary save still happened (UpdateShiftProfileAsync called).
        await _shiftMgmt.Received(1).UpdateShiftProfileAsync(Arg.Any<VolunteerEventProfile>());
        // Redirect lands on /Shifts (not Home/Index) even though signup failed.
        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ControllerName.Should().Be("Shifts");
    }
}
```

- [ ] **Step 6: Run to verify failure**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ProfileControllerDietaryMedicalReplayTests"
```

- [ ] **Step 7: Update `ProfileController.DietaryMedical` POST** — replace the final block (`SetSuccess(...); return RedirectToAction("Index", "Home");`) with branch handling. Full replacement of the POST action below; preserve existing validation logic and exception handling:

```csharp
[HttpPost("Me/DietaryMedical")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> DietaryMedical(DietaryMedicalViewModel model)
{
    if (!ModelState.IsValid) return View(model);

    if (model.Allergies.Contains(DietaryMedicalViewModel.OtherOption) && string.IsNullOrWhiteSpace(model.AllergyOtherText))
    {
        ModelState.AddModelError(nameof(model.AllergyOtherText), _localizer["Profile_DietaryMedical_AllergyOther_Required"].Value);
        return View(model);
    }
    if (model.Intolerances.Contains(DietaryMedicalViewModel.OtherOption) && string.IsNullOrWhiteSpace(model.IntoleranceOtherText))
    {
        ModelState.AddModelError(nameof(model.IntoleranceOtherText), _localizer["Profile_DietaryMedical_IntoleranceOther_Required"].Value);
        return View(model);
    }

    try
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return NotFound();

        var profile = await _shiftMgmt.GetOrCreateShiftProfileAsync(user.Id);
        model.ApplyTo(profile);
        await _shiftMgmt.UpdateShiftProfileAsync(profile);

        // Replay branches — see docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md
        switch (model.ReturnAction)
        {
            case "signup" when model.ShiftId is { } sid:
            {
                var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
                var result = await _signupService.SignUpAsync(user.Id, sid, actorUserId: null, isPrivileged: privileged);
                // extract on 3rd copy — see ShiftsController.SignUp
                if (!result.Success)
                    SetError(result.Error ?? "Shift signup failed.");
                else
                    SetSuccess(result.Warning is not null
                        ? $"Signed up successfully. Note: {result.Warning}"
                        : "Signed up successfully!");
                return RedirectToAction("Index", "Shifts");
            }
            case "signuprange" when model.RotaId is { } rid
                                     && model.StartDayOffset is { } sd
                                     && model.EndDayOffset is { } ed:
            {
                var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
                var result = await _signupService.SignUpRangeAsync(user.Id, rid, sd, ed, actorUserId: null, isPrivileged: privileged, skipConflicts: true);
                // extract on 3rd copy — see ShiftsController.SignUpRange
                if (!result.Success)
                    SetError(result.Error ?? "Shift range signup failed.");
                else
                    SetSuccess(result.Warning is not null
                        ? $"Signed up for date range. Note: {result.Warning}"
                        : "Signed up for date range!");
                return RedirectToAction("Index", "Shifts");
            }
            case "shifts":
                SetSuccess(_localizer["Profile_DietaryMedical_Saved"].Value);
                return RedirectToAction("Index", "Shifts");
            default:
                SetSuccess(_localizer["Profile_DietaryMedical_Saved"].Value);
                return RedirectToAction("Index", "Home");
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to save dietary/medical info");
        SetError(_localizer["Profile_DietaryMedical_SaveFailed"].Value);
        return View(model);
    }
}
```

- [ ] **Step 8: Run tests to verify pass**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ProfileControllerDietaryMedicalReplayTests"
```

- [ ] **Step 9: Build the full solution** to catch any DI-wiring or view-binding issues:

```bash
dotnet build Humans.slnx -v quiet
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 10: Commit**

```bash
git add src/Humans.Web/Controllers/ProfileController.cs \
        src/Humans.Web/Models/DietaryMedicalViewModel.cs \
        src/Humans.Web/Views/Profile/DietaryMedical.cshtml \
        tests/Humans.Application.Tests/Controllers/ProfileControllerDietaryMedicalReplayTests.cs
git commit -m "feat(profile): DietaryMedical POST replays signup/signuprange

After a successful dietary save, branches on the carryover ReturnAction:
- signup + shiftId   ⇒ replays SignUpAsync, lands on /Shifts with flash
- signuprange + ids  ⇒ replays SignUpRangeAsync, lands on /Shifts with flash
- shifts             ⇒ lands on /Shifts with the save-success flash
- (default)          ⇒ lands on Home/Index (existing behavior)

ProfileController now depends on IShiftSignupService for the replay
paths; the inline flash mapping duplicates ShiftsController's two
existing inline copies — extract on the third call site per project
doctrine.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 5: Dietary-missing banner

A new view component that renders a red banner at the top of `/Shifts` and `/Shifts/Mine` when the user has a qualifying signup and no dietary on file.

### Task 5.1: `DietaryMissingBannerViewComponent` + view + tests

**Files:**
- Create: `src/Humans.Web/ViewComponents/DietaryMissingBannerViewComponent.cs`
- Create: `src/Humans.Web/Views/Shared/Components/DietaryMissingBanner/Default.cshtml`
- Create: `tests/Humans.Application.Tests/ViewComponents/DietaryMissingBannerViewComponentTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Humans.Application.Tests/ViewComponents/DietaryMissingBannerViewComponentTests.cs
public class DietaryMissingBannerViewComponentTests
{
    [Fact]
    public async Task Renders_WhenQualifyingSignupAndDietaryEmpty()
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(true);
        _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false)
                  .Returns(new VolunteerEventProfile { DietaryPreference = null });

        var result = await _sut.InvokeAsync(userId);

        result.Should().BeOfType<ViewViewComponentResult>();
    }

    [Theory]
    [InlineData(true,  "Vegan")]      // dietary filled
    [InlineData(false, null)]          // no qualifying signup
    [InlineData(false, "Vegan")]       // neither
    public async Task DoesNotRender_WhenGateMissed(bool hasQualifying, string? dietary)
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId).Returns(hasQualifying);
        _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false)
                  .Returns(new VolunteerEventProfile { DietaryPreference = dietary });

        var result = await _sut.InvokeAsync(userId);

        result.Should().BeOfType<ContentViewComponentResult>()
              .Which.Content.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure** (compile error)

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~DietaryMissingBannerViewComponentTests"
```

- [ ] **Step 3: Implement the view component.** Takes `userId` as an invocation parameter — the caller (the parent view) passes it from the page view model. Chunk 6.1 adds `UserId` to both top-level view models (`ShiftBrowseViewModel`, `MyShiftsViewModel`) so the views can invoke this component without resolving the user themselves.

```csharp
// src/Humans.Web/ViewComponents/DietaryMissingBannerViewComponent.cs
using Humans.Application.Interfaces.Shifts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class DietaryMissingBannerViewComponent : ViewComponent
{
    private readonly IShiftManagementService _shiftMgmt;

    public DietaryMissingBannerViewComponent(IShiftManagementService shiftMgmt)
    {
        _shiftMgmt = shiftMgmt;
    }

    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var hasQualifyingSignup = await _shiftMgmt.HasQualifyingCantinaSignupAsync(userId);
        if (!hasQualifyingSignup) return Content(string.Empty);

        var profile = await _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false);
        if (!string.IsNullOrEmpty(profile?.DietaryPreference)) return Content(string.Empty);

        return View();
    }
}
```

- [ ] **Step 4: Implement the view**:

```html
@* src/Humans.Web/Views/Shared/Components/DietaryMissingBanner/Default.cshtml *@
@inject Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> Localizer
<div class="alert alert-danger d-flex align-items-center mb-3" role="alert">
    <i class="fa-solid fa-utensils me-2"></i>
    <div class="flex-grow-1">
        <strong>@Localizer["DietaryMissingBanner_Title"]</strong>
        <div class="small">@Localizer["DietaryMissingBanner_Body"]</div>
    </div>
    <a class="btn btn-sm btn-primary ms-3"
       asp-controller="Profile" asp-action="DietaryMedical"
       asp-route-returnAction="shifts">
        @Localizer["DietaryMissingBanner_Cta"]
    </a>
</div>
```

- [ ] **Step 5: Run tests to verify pass**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~DietaryMissingBannerViewComponentTests"
```

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/ViewComponents/DietaryMissingBannerViewComponent.cs \
        src/Humans.Web/Views/Shared/Components/DietaryMissingBanner/Default.cshtml \
        tests/Humans.Application.Tests/ViewComponents/DietaryMissingBannerViewComponentTests.cs
git commit -m "feat(shifts): DietaryMissingBannerViewComponent

Renders a red banner with CTA to /Profile/Me/DietaryMedical when the
user has a qualifying cantina signup and DietaryPreference is empty.
Returns empty content otherwise. To be invoked from /Shifts/Index and
/Shifts/Mine.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 5.2: Invoke banner from `/Shifts/Index` and `/Shifts/Mine`

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml`
- Modify: `src/Humans.Web/Views/Shifts/Mine.cshtml`

- [ ] **Step 1: Add the banner invocation** at the top of both views, just below the page heading and above any rota tables. The view passes `Model.UserId` to the component; Chunk 6.1 adds the `UserId` property to both top-level view models.

Add to `Index.cshtml` near the top of the page-content area:

```html
@await Component.InvokeAsync("DietaryMissingBanner", new { userId = Model.UserId })
```

Same to `Mine.cshtml`.

Note: this task depends on Chunk 6.1 landing first (or being landed in the same commit, if preferred) — it adds `UserId` to `ShiftBrowseViewModel` and `MyShiftsViewModel`. If executing strictly in order, you may need to either (a) reorder so Chunk 6.1 lands before Chunk 5.2, or (b) include the `UserId` addition in Chunk 5.2's commit. Option (a) is cleaner; the plan presents chunks in conceptual order, not strict execution order.

- [ ] **Step 2: Build and smoke**

```bash
dotnet build Humans.slnx -v quiet
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shifts/Index.cshtml \
        src/Humans.Web/Views/Shifts/Mine.cshtml
git commit -m "feat(shifts): invoke DietaryMissingBanner on /Shifts and /Shifts/Mine

Renders the dietary-missing banner at the top of both shift-browse
views; the view component itself decides whether to draw anything based
on the user's current state.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 6: Rota-table Sign-Up button lockout

Add a `SignupsBlockedByMissingDietary` flag that flows from the controller through the page view models into each rota-partial view model, and render disabled Sign-Up buttons when the flag is true.

### Task 6.1: Add flag to all four view models

**Files:**
- Modify: `src/Humans.Web/Models/ShiftViewModels.cs`

- [ ] **Step 1: Add `public bool SignupsBlockedByMissingDietary { get; set; }`** to four classes in this file:
  - `ShiftBrowseViewModel`
  - `MyShiftsViewModel`
  - `EventRotaTableViewModel`
  - `BuildStrikeRotaTableViewModel`

Also add `public Guid UserId { get; set; }` to `ShiftBrowseViewModel` and `MyShiftsViewModel` (needed by the banner invoke in Chunk 5.2; not currently on either VM — confirmed by grep). The rota-table VMs do not need `UserId`.

No tests for this step alone — it's pure data plumbing; covered by integration with the partials in 6.3.

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/ShiftViewModels.cs
git commit -m "feat(shifts): SignupsBlockedByMissingDietary flag on view models

Adds the flag to ShiftBrowseViewModel, MyShiftsViewModel,
EventRotaTableViewModel, and BuildStrikeRotaTableViewModel — the rota
partials will use it to render disabled Sign-Up buttons in a follow-up.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 6.2: Compute the flag in `ShiftsController` and set it on the top-level VMs

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs`

- [ ] **Step 1: Extract a private helper** to compute the flag once per request (it's the same boolean the banner uses; we keep this in the controller to avoid duplicating the `HasQualifyingCantinaSignup` + dietary-empty check):

```csharp
private async Task<bool> ComputeSignupsBlockedByMissingDietaryAsync(Guid userId, CancellationToken ct = default)
{
    if (!await _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, ct)) return false;
    var profile = await _shiftMgmt.GetShiftProfileAsync(userId, includeMedical: false);
    return string.IsNullOrEmpty(profile?.DietaryPreference);
}
```

- [ ] **Step 2: Set the flag + UserId** when constructing `ShiftBrowseViewModel` in `Index` (around line 208) and `MyShiftsViewModel` in `Mine` (around line 369). E.g.:

```csharp
var model = new ShiftBrowseViewModel
{
    // ... existing init ...
    UserId = user.Id,
    SignupsBlockedByMissingDietary = await ComputeSignupsBlockedByMissingDietaryAsync(user.Id, HttpContext.RequestAborted),
};
```

- [ ] **Step 3: Build**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/ShiftsController.cs
git commit -m "feat(shifts): compute SignupsBlockedByMissingDietary in controller

ShiftsController.Index/Mine now compute the lockout flag once per
request via a private helper that checks HasQualifyingCantinaSignup +
empty DietaryPreference. The flag is set on the top-level view models;
views propagate it to each rota-partial construction site.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 6.3: Pass flag down from views to each rota-partial

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml`
- Modify: `src/Humans.Web/Views/Shifts/Mine.cshtml`

- [ ] **Step 1: List every construction site** in both views:

```bash
grep -nE "new EventRotaTableViewModel|new BuildStrikeRotaTableViewModel" src/Humans.Web/Views/Shifts/Index.cshtml src/Humans.Web/Views/Shifts/Mine.cshtml
```

Edit each one to add `SignupsBlockedByMissingDietary = Model.SignupsBlockedByMissingDietary,`. If `Mine.cshtml` doesn't construct a rota-table VM directly (it may render via a different partial pattern), the grep tells you — skip what doesn't apply.

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shifts/Index.cshtml \
        src/Humans.Web/Views/Shifts/Mine.cshtml
git commit -m "feat(shifts): pass dietary-lockout flag down to rota partials

Every EventRotaTableViewModel / BuildStrikeRotaTableViewModel
construction site in /Shifts/Index and /Shifts/Mine now propagates
Model.SignupsBlockedByMissingDietary to the partial.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 6.4: Render disabled Sign-Up button in `_EventRotaTable` and `_BuildStrikeRotaTable`

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_EventRotaTable.cshtml`
- Modify: `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml`

- [ ] **Step 1: Update `_EventRotaTable.cshtml`** around line 108. Replace:

```html
<button type="submit" class="btn btn-sm btn-success">@Localizer["Shifts_SignUpButton"]</button>
```

with:

```html
@if (Model.SignupsBlockedByMissingDietary)
{
    <button type="submit"
            class="btn btn-sm btn-success"
            disabled
            aria-disabled="true"
            title="@Localizer["Shifts_SignupDisabledTooltip_MissingDietary"]">
        @Localizer["Shifts_SignUpButton"]
    </button>
}
else
{
    <button type="submit" class="btn btn-sm btn-success">@Localizer["Shifts_SignUpButton"]</button>
}
```

- [ ] **Step 2: Apply the same change to `_BuildStrikeRotaTable.cshtml`** — find the existing Sign-Up submit button (the spec exploration noted the form for date-range signup; it's around line ~50). Apply the same conditional swap.

- [ ] **Step 3: Build**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Shared/_EventRotaTable.cshtml \
        src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml
git commit -m "feat(shifts): disable Sign-Up buttons when dietary missing

When SignupsBlockedByMissingDietary is true on the rota-partial view
model, the Sign-Up button renders disabled with an aria-disabled flag
and a tooltip pointing at the dietary form. Defense-in-depth: the
controller-level redirect catches any POST that slips past the disabled
button.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Chunk 7: Docs + final verification

### Task 7.1: Append US-35.5 + US-35.6 to feature spec

**Files:**
- Modify: `docs/features/profiles/dietary-medical-nudge.md`

- [ ] **Step 1: Append two new user stories** at the end of the existing US section, referencing this design's filename:

```markdown
### US-35.5: Nudge appears before any shift signup
**As a** newly registered human with no shifts yet
**I want to** be reminded to record my dietary and medical info
**So that** the cantina has it on file the moment I sign up for a long shift

**Acceptance Criteria:**
- The dashboard `ThingsToDoViewComponent` dietary item appears whenever
  `VolunteerEventProfile.DietaryPreference` is empty, regardless of signups.
- Description uses `Todo_DietaryMedical_NoShift_Pending` when there is no
  qualifying signup; the existing `_Pending` copy is used otherwise.
- See `docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md`.

### US-35.6: Hard gate on qualifying-shift signup
**As** the system
**I want to** block sign-up for a 6+ hour shift until the human's dietary
preference is on file
**So that** the cantina never gets blindsided

**Acceptance Criteria:**
- `ShiftsController.SignUp` / `SignUpRange` redirect to
  `/Profile/Me/DietaryMedical?returnAction=signup|signuprange&...` when the
  target shift `QualifiesForCantinaMeal()` and `DietaryPreference` is empty.
- On successful save, the form replays the signup.
- A banner on `/Shifts` and `/Shifts/Mine` plus disabled Sign-Up buttons
  catch humans who already have a qualifying signup but no dietary on file.
- See `docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md`.
```

- [ ] **Step 2: Commit**

```bash
git add docs/features/profiles/dietary-medical-nudge.md
git commit -m "docs(features): US-35.5 + US-35.6 for dietary prompt tightening

References the new design at docs/superpowers/specs/2026-05-25-...

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

### Task 7.2: Full build + test sweep

- [ ] **Step 1: Full build**

```bash
dotnet build Humans.slnx -v quiet
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Full test run**

```bash
dotnet test Humans.slnx -v quiet
```
Expected: All `Humans.Application.Tests`, `Humans.Domain.Tests`, `Humans.Web.Tests`, and `Humans.Analyzers.Tests` projects pass. `Humans.Integration.Tests` may have pre-existing failures unrelated to this change — verify any failures are in that project and match the pre-change baseline.

### Task 7.3: Manual smoke (via the `run` skill)

- [ ] **Step 1: Launch the app**

```bash
dotnet run --project src/Humans.Web
```

- [ ] **Step 2: Walk the three flows** in the browser at `http://localhost:5000` (or whatever port the app prints):

  1. **Fresh user, no signups:** dashboard shows the dietary card with `_NoShift` copy. Click → fill → save → card gone, redirect to dashboard.
  2. **User with no dietary clicks Sign Up on a 6+ hr shift:** redirect to `/Profile/Me/DietaryMedical?returnAction=signup&shiftId=...` → fill → save → land on `/Shifts` with the signup success flash AND the signup exists in the user's bookings.
  3. **User with a pre-existing qualifying signup but no dietary:** visit `/Shifts` → red banner at top, Sign-Up buttons disabled with tooltip. Click banner CTA → fill → save → land on `/Shifts` with banner gone, buttons enabled.

If any of those flows breaks, stop and diagnose before opening the PR.

### Task 7.4: Open the PR

- [ ] **Step 1: Push the branch**

```bash
git push -u fork feature/dietary-prompt-tightening
```

- [ ] **Step 2: Open the PR against `peterdrier/Humans` `main`**

```bash
gh pr create --repo peterdrier/Humans \
  --base main \
  --head FrankFanteev:feature/dietary-prompt-tightening \
  --title "Dietary prompt tightening — soft nudge always, hard gate at qualifying signups" \
  --body "$(cat <<'EOF'
## Summary

Tightens the existing dietary-medical nudge (issue #279) so the cantina never gets blindsided:

- Soft prompt fires whenever `DietaryPreference` is empty, even before any signup.
- `ShiftsController.SignUp` / `SignUpRange` redirect to the existing DietaryMedical form when the target shift qualifies for cantina meals and dietary is empty; `ProfileController.DietaryMedical` POST replays the signup after a successful save.
- New `DietaryMissingBannerViewComponent` + disabled Sign-Up buttons on `/Shifts` and `/Shifts/Mine` catch humans with a pre-existing qualifying signup and no dietary on file.

No new entity / column / migration; reuses existing `VolunteerEventProfile` fields and the `DietaryMedical` form.

**Spec:** `docs/superpowers/specs/2026-05-25-dietary-prompt-tightening-design.md`
**Plan:** `docs/superpowers/plans/2026-05-25-dietary-prompt-tightening.md`

## Depends on

This branch was developed against `fork/feature/issue-279-dietary-nudge-impl`. Merge the dietary-nudge PR first, or expect ~38 commits of overlap that will resolve cleanly on rebase.

## Test plan

- [ ] Fresh user, no signups: dashboard shows dietary card with `_NoShift` copy; fill → card disappears.
- [ ] User with no dietary clicks Sign Up on a 6+ hr shift: redirect to DietaryMedical → fill → land on `/Shifts` with signup success flash and signup persisted.
- [ ] User with pre-existing qualifying signup but no dietary: `/Shifts` shows banner, Sign-Up buttons disabled. Banner CTA → fill → buttons re-enabled.
- [ ] User with dietary filled: no card, no banner, no redirect (regression).
- [ ] `dotnet test Humans.slnx -v quiet` — Application/Domain/Web/Analyzers tests green; Integration baseline unchanged.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 3: Report the PR URL** back to the user and close out the implementation.

---

## Chunk 8 — Meal preference + allergies on Profile Edit (added mid-implementation)

**Not in the original plan.** Added during execution at the user's request after smoke-testing: surface **meal preference + allergies** under the **General Information** section of `/Profile/Me/Edit`, as a second entry point alongside the dedicated `/Profile/Me/DietaryMedical` page (which stays). Decisions confirmed with the user: keep the DietaryMedical page; only meal preference + allergies on Edit (intolerances + medical conditions remain owned by the DietaryMedical page, medical being GDPR Art. 9 health data).

**Files:**
- `src/Humans.Web/Models/ProfileViewModel.cs` — added `DietaryPreference`, `Allergies` (`List<string>`), `AllergyOtherText`.
- `src/Humans.Web/Controllers/ProfileController.cs` — Edit GET populates the 3 fields from `_shiftMgmt.GetShiftProfileAsync(includeMedical:false)`; Edit POST validates the allergy-Other rule, then `GetOrCreateShiftProfileAsync` → sets ONLY `DietaryPreference` + filtered `Allergies` + `AllergyOtherText` → `UpdateShiftProfileAsync`. **Load-bearing invariant:** loads the existing profile and never touches `Intolerances` / `IntoleranceOtherText` / `MedicalConditions` (regression-tested).
- `src/Humans.Web/Views/Profile/Edit.cshtml` — meal-pref radio + allergy chips + "Other" reveal under General Information, mirroring `DietaryMedical.cshtml`, reusing existing `Profile_DietaryMedical_*` resource keys; CSP-nonce JS toggle for the Other reveal.
- Tests: 4 added to `tests/Humans.Application.Tests/Controllers/ProfileControllerEditTests.cs` (writes dietary, does-NOT-clobber intolerances/medical regression, allergy-Other-invalid validation, GET populates).

**Commit:** `3d9bc4e8`.

## Execution record (2026-05-25)

- All 8 chunks implemented via subagent-driven development (implementer + code-quality review per task; spec-review skipped per user after Chunk 1).
- `dotnet test tests/Humans.Application.Tests` → **2599 passed, 0 failed, 1 skipped** on final branch (incl. architecture baselines + SurfaceBudget tests).
- Manual browser smoke test PASSED for the production case (approved active volunteer + qualifying signup + empty dietary): dashboard nudge (qualifying copy), `/Shifts` red banner, all sign-up buttons disabled with tooltip, "Tell us now" → DietaryMedical form (`returnAction=shifts` carryover) → save → redirect to `/Shifts`, banner gone + buttons re-enabled. Negative case (dietary filled → no nudge) also confirmed.
- Security review: no HIGH/MEDIUM-confidence vulnerabilities (CSV formula-injection neutralized, medical excluded from cantina roster, signup-replay owner-scoped, no unsafe `Html.Raw`).
- Repo-rule check: `IShiftSignupService` is not `[SurfaceBudget]`-ratcheted, so `PeekRangeShiftsAsync` adds no budgeted surface; zero methods added to any budgeted interface.

**Known follow-up (not blocking):** `DietaryPreference` is persisted as any non-blank string; spec US-35.2 expects validation against the four `DietaryOptions.DietaryPreferences` values. Same gap exists on the pre-existing `DietaryMedical` page. Low risk (Razor escapes on render, CSV writer neutralizes formulas). Tighten in both places if desired.
