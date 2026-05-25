# Low-Friction Shift Signup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the guided onboarding widget (`/OnboardingWidget`) per the design at `docs/superpowers/specs/2026-05-05-low-friction-shift-signup-design.md` — three steps (Names → Shifts → Consents), entered via `/Welcome`, with Pending-until-consents shift signups promoted automatically on admission.

**Architecture:** Web-layer controller + three views + one Application-layer query service (`IOnboardingWidgetState`). One new method on `IShiftSignupService` (`PromoteWidgetPendingSignupsAfterAdmissionAsync`) and one branch in `SignUpAsync` / `SignUpRangeAsync` that forces `Pending` when the user is missing required Volunteer consents. No new tables, no migration, no changes to `MembershipRequiredFilter`.

**Tech Stack:** ASP.NET Core 10 MVC, EF Core 10 / PostgreSQL, NodaTime, Bootstrap 5 + Razor partials. Tests in xUnit (`tests/Humans.Application.Tests`, `tests/Humans.Web.Tests`, etc.).

**Branch:** `low-friction-shift-signup` (worktree: `H:\source\Humans\.worktrees\low-friction-shift-signup\`). Spec already committed on this branch.

**Constructor-growth note:** `OnboardingWidgetController` accumulates dependencies across Tasks 2/3/5/7 (`IOnboardingWidgetState` → `+IProfileService` → `+IShiftSignupService` + `IShiftManagementService` → `+IConsentService`). When a later task adds a parameter, **update every prior task's `BuildSut` helper** in the same task to pass a default `Mock<>` for the new dep. Don't ship a task that leaves earlier tests failing to compile.

**Service-method verification:** before implementing each task, grep the interface to confirm the methods named below exist with the signatures shown:
- `IShiftManagementService.GetActiveAsync` (used Tasks 1, 8 — confirmed exists; called from `HomeController.IndexCore`).
- `IShiftSignupService.GetActiveSignupStatusesAsync(userId, eventSettingsId)` returning `(HashSet<Guid>, Dictionary<Guid, SignupStatus>)` (confirmed in interface).
- `IProfileService.SaveProfileAsync(userId, displayName, ProfileSaveRequest, language, ct)` (confirmed) — the `ProfileSaveRequest` type lives next to the service interface; read it before constructing one in Task 3.
- `IConsentService.SubmitConsentAsync(userId, documentVersionId, ct)` returning `ConsentSubmitResult` (confirmed).
- `IConsentService.GetRequiredConsentRowsForUserAsync` (Task 7) — **not yet verified**. If it doesn't exist, look for an existing read that returns required-docs-with-signed-status (e.g., something on `IConsentService` or `IDocumentService`); if none, add a pure read method following the existing list-read pattern in the same interface.

**Build/test commands** (from worktree root):
```
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```
The `-v quiet` is required (see `memory/process/dotnet-verbosity-quiet.md`).

---

## Pre-flight

- [ ] **Step 1: Confirm worktree + branch**

```bash
pwd  # expect: H:\source\Humans\.worktrees\low-friction-shift-signup or /h/source/Humans/.worktrees/low-friction-shift-signup
git branch --show-current  # expect: low-friction-shift-signup
git log --oneline -3
```

- [ ] **Step 2: Baseline build + test**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```
Both must pass before starting work. If they don't, stop and surface to the user.

---

## Task 1: `IOnboardingWidgetState` interface, impl, and DI registration

**Files:**
- Create: `src/Humans.Application/Interfaces/Onboarding/IOnboardingWidgetState.cs`
- Create: `src/Humans.Application/Services/Onboarding/OnboardingWidgetState.cs`
- Modify: `src/Humans.Web/Extensions/Sections/GovernanceSectionExtensions.cs` (the section file that already registers `IOnboardingService`) — add the new service registration alongside.
- Test: `tests/Humans.Application.Tests/Services/Onboarding/OnboardingWidgetStateTests.cs`

The `OnboardingShiftSkip` session key is used by Step 2 to remember a "Not right now" click within the browser session. It's read here and written by the controller's `Skip` action in Task 5.

- [ ] **Step 1: Write the interface + enum**

`src/Humans.Application/Interfaces/Onboarding/IOnboardingWidgetState.cs`:

```csharp
namespace Humans.Application.Interfaces.Onboarding;

/// <summary>
/// Returns which step of the onboarding widget a user should be routed to.
/// Reads existing data (Profile, current-event signups, required consents) plus a
/// per-session "shift skip" flag set by the widget's Step 2 "Not right now" action.
/// No new tables; no new claims.
/// </summary>
public interface IOnboardingWidgetState
{
    Task<OnboardingWidgetStep> GetCurrentStepAsync(Guid userId, CancellationToken ct = default);
}

public enum OnboardingWidgetStep
{
    Names = 0,
    Shifts = 1,
    Consents = 2,
    Complete = 3,
}
```

- [ ] **Step 2: Write the failing test**

`tests/Humans.Application.Tests/Services/Onboarding/OnboardingWidgetStateTests.cs`:

```csharp
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Onboarding;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Humans.Application.Tests.Services.Onboarding;

public class OnboardingWidgetStateTests
{
    private readonly Mock<IProfileService> _profile = new();
    private readonly Mock<IShiftSignupService> _signups = new();
    private readonly Mock<IMembershipCalculator> _membership = new();
    private readonly Mock<IShiftManagementService> _shiftMgmt = new();
    private readonly Mock<IHttpContextAccessor> _http = new();
    private readonly DefaultHttpContext _httpContext = new();

    public OnboardingWidgetStateTests()
    {
        _http.SetupGet(h => h.HttpContext).Returns(_httpContext);
        // Session is provided by a no-op test session in the helper below.
        _httpContext.Session = new TestSession();
    }

    private OnboardingWidgetState BuildSut() =>
        new(_profile.Object, _signups.Object, _membership.Object, _shiftMgmt.Object, _http.Object);

    [Fact]
    public async Task ConsentsComplete_ShortCircuitsToComplete_EvenWithoutSignup()
    {
        var userId = Guid.NewGuid();
        _membership.Setup(m => m.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default))
            .ReturnsAsync(true);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Complete, step);
    }

    [Fact]
    public async Task NoProfile_ReturnsNames()
    {
        var userId = Guid.NewGuid();
        _membership.Setup(m => m.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default))
            .ReturnsAsync(false);
        _profile.Setup(p => p.GetProfileAsync(userId, default)).ReturnsAsync((Profile?)null);

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Names, step);
    }

    [Fact]
    public async Task ProfileButNoSignupAndNoSkip_ReturnsShifts()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _membership.Setup(m => m.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default))
            .ReturnsAsync(false);
        _profile.Setup(p => p.GetProfileAsync(userId, default))
            .ReturnsAsync(new Profile { UserId = userId, BurnerName = "x", FirstName = "y", LastName = "z" });
        _shiftMgmt.Setup(s => s.GetActiveAsync())
            .ReturnsAsync(new EventSettings { Id = eventId });
        _signups.Setup(s => s.GetActiveSignupStatusesAsync(userId, eventId))
            .ReturnsAsync((new HashSet<Guid>(), new Dictionary<Guid, SignupStatus>()));

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Shifts, step);
    }

    [Fact]
    public async Task ProfileWithSkipFlag_ReturnsConsents()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        _membership.Setup(m => m.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default))
            .ReturnsAsync(false);
        _profile.Setup(p => p.GetProfileAsync(userId, default))
            .ReturnsAsync(new Profile { UserId = userId });
        _shiftMgmt.Setup(s => s.GetActiveAsync())
            .ReturnsAsync(new EventSettings { Id = eventId });
        _signups.Setup(s => s.GetActiveSignupStatusesAsync(userId, eventId))
            .ReturnsAsync((new HashSet<Guid>(), new Dictionary<Guid, SignupStatus>()));
        _httpContext.Session.SetString(OnboardingWidgetState.ShiftSkipSessionKey, "true");

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
    }

    [Fact]
    public async Task ProfileWithCurrentEventSignup_ReturnsConsents()
    {
        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _membership.Setup(m => m.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, default))
            .ReturnsAsync(false);
        _profile.Setup(p => p.GetProfileAsync(userId, default))
            .ReturnsAsync(new Profile { UserId = userId });
        _shiftMgmt.Setup(s => s.GetActiveAsync())
            .ReturnsAsync(new EventSettings { Id = eventId });
        _signups.Setup(s => s.GetActiveSignupStatusesAsync(userId, eventId))
            .ReturnsAsync((new HashSet<Guid> { shiftId },
                           new Dictionary<Guid, SignupStatus> { [shiftId] = SignupStatus.Pending }));

        var step = await BuildSut().GetCurrentStepAsync(userId);

        Assert.Equal(OnboardingWidgetStep.Consents, step);
    }

    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _store = new();
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => _store.Keys;
        public void Clear() => _store.Clear();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public void Set(string key, byte[] value) => _store[key] = value;
        public bool TryGetValue(string key, out byte[] value)
        {
            if (_store.TryGetValue(key, out var v)) { value = v; return true; }
            value = Array.Empty<byte>();
            return false;
        }
    }
}
```

- [ ] **Step 3: Run the tests, confirm they fail (compile error — service does not exist yet)**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~OnboardingWidgetStateTests" -v quiet
```
Expected: build error referencing `OnboardingWidgetState`.

- [ ] **Step 4: Implement the service**

`src/Humans.Application/Services/Onboarding/OnboardingWidgetState.cs`:

```csharp
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Http;

namespace Humans.Application.Services.Onboarding;

public class OnboardingWidgetState : IOnboardingWidgetState
{
    /// <summary>Session key set by `/OnboardingWidget/Skip` and read here.</summary>
    public const string ShiftSkipSessionKey = "OnboardingShiftSkip";

    private readonly IProfileService _profile;
    private readonly IShiftSignupService _signups;
    private readonly IMembershipCalculator _membership;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IHttpContextAccessor _http;

    public OnboardingWidgetState(
        IProfileService profile,
        IShiftSignupService signups,
        IMembershipCalculator membership,
        IShiftManagementService shiftMgmt,
        IHttpContextAccessor http)
    {
        _profile = profile;
        _signups = signups;
        _membership = membership;
        _shiftMgmt = shiftMgmt;
        _http = http;
    }

    public async Task<OnboardingWidgetStep> GetCurrentStepAsync(Guid userId, CancellationToken ct = default)
    {
        // Consents-complete short-circuits everyone past the widget.
        if (await _membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, ct))
            return OnboardingWidgetStep.Complete;

        var profile = await _profile.GetProfileAsync(userId, ct);
        if (profile is null)
            return OnboardingWidgetStep.Names;

        var hasSkip = string.Equals(
            _http.HttpContext?.Session.GetString(ShiftSkipSessionKey),
            "true",
            StringComparison.Ordinal);

        var activeEvent = await _shiftMgmt.GetActiveAsync();
        var hasCurrentEventSignup = false;
        if (activeEvent is not null)
        {
            var (shiftIds, _) = await _signups.GetActiveSignupStatusesAsync(userId, activeEvent.Id);
            hasCurrentEventSignup = shiftIds.Count > 0;
        }

        return (hasSkip || hasCurrentEventSignup)
            ? OnboardingWidgetStep.Consents
            : OnboardingWidgetStep.Shifts;
    }
}
```

- [ ] **Step 5: Register in DI**

In `src/Humans.Web/Extensions/Sections/GovernanceSectionExtensions.cs` find the existing `services.AddScoped<IOnboardingService>(...)` line and add directly above or below it:

```csharp
services.AddScoped<IOnboardingWidgetState, Humans.Application.Services.Onboarding.OnboardingWidgetState>();
```

If `IHttpContextAccessor` is not already registered, add:

```csharp
services.AddHttpContextAccessor();
```

(Search Program.cs / extensions for `AddHttpContextAccessor` first; if found, skip.)

- [ ] **Step 6: Run tests — expect pass**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~OnboardingWidgetStateTests" -v quiet
```

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/Interfaces/Onboarding/IOnboardingWidgetState.cs \
        src/Humans.Application/Services/Onboarding/OnboardingWidgetState.cs \
        src/Humans.Web/Extensions/Sections/GovernanceSectionExtensions.cs \
        tests/Humans.Application.Tests/Services/Onboarding/OnboardingWidgetStateTests.cs
git commit -m "$(cat <<'EOF'
feat(onboarding): add IOnboardingWidgetState query service

Returns which widget step a user should be routed to based on existing
data (Profile, current-event signups, required Volunteer consents) plus
a per-session "shift skip" flag. Consents-complete short-circuits to
Complete; the rest of the rules are evaluated top-down per the spec.

No new tables.
EOF
)"
```

---

## Task 2: `OnboardingWidgetController` skeleton + Index dispatcher

**Files:**
- Create: `src/Humans.Web/Controllers/OnboardingWidgetController.cs`
- Test: `tests/Humans.Web.Tests/Controllers/OnboardingWidgetControllerTests.cs` (create the test project file structure if it doesn't exist; otherwise place under existing Web tests folder).

The dispatcher (`Index`) is the canonical entry point — `/Welcome`, the Home/Guest redirects, and the layout banner all link here. Other actions (`Names`, `Shifts`, `Consents`, `Finish`) are stubbed for now and filled in by Tasks 3, 5, 7.

- [ ] **Step 1: Write failing controller tests**

```csharp
using System.Security.Claims;
using Humans.Application.Interfaces.Onboarding;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Humans.Web.Tests.Controllers;

public class OnboardingWidgetControllerDispatcherTests
{
    private readonly Mock<IOnboardingWidgetState> _state = new();

    private static OnboardingWidgetController BuildSut(
        Mock<IOnboardingWidgetState> state, Guid userId)
    {
        var ctrl = new OnboardingWidgetController(state.Object);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    [Theory]
    [InlineData(OnboardingWidgetStep.Names, "Names")]
    [InlineData(OnboardingWidgetStep.Shifts, "Shifts")]
    [InlineData(OnboardingWidgetStep.Consents, "Consents")]
    public async Task Index_RedirectsToCurrentStep(OnboardingWidgetStep step, string action)
    {
        var userId = Guid.NewGuid();
        _state.Setup(s => s.GetCurrentStepAsync(userId, default)).ReturnsAsync(step);
        var ctrl = BuildSut(_state, userId);

        var result = await ctrl.Index(default);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(action, redirect.ActionName);
        Assert.Equal("OnboardingWidget", redirect.ControllerName);
    }

    [Fact]
    public async Task Index_RedirectsToHome_WhenComplete()
    {
        var userId = Guid.NewGuid();
        _state.Setup(s => s.GetCurrentStepAsync(userId, default))
            .ReturnsAsync(OnboardingWidgetStep.Complete);
        var ctrl = BuildSut(_state, userId);

        var result = await ctrl.Index(default);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("Home", redirect.ControllerName);
    }
}
```

- [ ] **Step 2: Run — expect compile failure (controller does not exist)**

```bash
dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~OnboardingWidgetControllerDispatcherTests" -v quiet
```

- [ ] **Step 3: Create the controller skeleton**

`src/Humans.Web/Controllers/OnboardingWidgetController.cs`:

```csharp
using System.Security.Claims;
using Humans.Application.Interfaces.Onboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Guided onboarding widget — three steps (Names → Shifts → Consents).
/// Index is the canonical dispatcher; /Welcome, Home/Index, Guest/Index, and the
/// layout banner all link here without needing to know which step a user is on.
/// </summary>
[Authorize]
public class OnboardingWidgetController : Controller
{
    private readonly IOnboardingWidgetState _state;

    public OnboardingWidgetController(IOnboardingWidgetState state)
    {
        _state = state;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = GetUserId();
        var step = await _state.GetCurrentStepAsync(userId, ct);

        return step switch
        {
            OnboardingWidgetStep.Names => RedirectToAction(nameof(Names)),
            OnboardingWidgetStep.Shifts => RedirectToAction(nameof(Shifts)),
            OnboardingWidgetStep.Consents => RedirectToAction(nameof(Consents)),
            OnboardingWidgetStep.Complete => RedirectToAction("Index", "Home"),
            _ => RedirectToAction("Index", "Home"),
        };
    }

    [HttpGet]
    public IActionResult Names() => throw new NotImplementedException();

    [HttpGet]
    public IActionResult Shifts() => throw new NotImplementedException();

    [HttpGet]
    public IActionResult Consents() => throw new NotImplementedException();

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
```

- [ ] **Step 4: Run tests — expect pass**

```bash
dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~OnboardingWidgetControllerDispatcherTests" -v quiet
```

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/OnboardingWidgetController.cs \
        tests/Humans.Web.Tests/Controllers/OnboardingWidgetControllerDispatcherTests.cs
git commit -m "feat(onboarding): add OnboardingWidgetController dispatcher"
```

---

## Task 3: Step 1 — Names (view + GET/POST)

**Files:**
- Create: `src/Humans.Web/Models/OnboardingWidget/NamesViewModel.cs`
- Create: `src/Humans.Web/Views/OnboardingWidget/Names.cshtml`
- Modify: `src/Humans.Web/Controllers/OnboardingWidgetController.cs` — replace the `Names` stub with GET + POST.
- Test: `tests/Humans.Web.Tests/Controllers/OnboardingWidgetControllerNamesTests.cs`

The POST writes via the existing `IProfileService.SaveProfileAsync(userId, displayName, ProfileSaveRequest, language, ct)`. Look at how `ProfileController.Edit` already calls it for the parameter pattern; we pass a minimal `ProfileSaveRequest` containing only the three name fields.

- [ ] **Step 1: ViewModel**

`src/Humans.Web/Models/OnboardingWidget/NamesViewModel.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models.OnboardingWidget;

public class NamesViewModel
{
    [Required]
    [StringLength(100)]
    [Display(Name = "Burner Name")]
    public string BurnerName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Legal First Name")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Legal Last Name(s)")]
    public string LastName { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Failing test — POST happy path**

In `tests/Humans.Web.Tests/Controllers/OnboardingWidgetControllerNamesTests.cs`:

```csharp
using System.Security.Claims;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Web.Controllers;
using Humans.Web.Models.OnboardingWidget;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Humans.Web.Tests.Controllers;

public class OnboardingWidgetControllerNamesTests
{
    private readonly Mock<IOnboardingWidgetState> _state = new();
    private readonly Mock<IProfileService> _profile = new();

    private OnboardingWidgetController BuildSut(Guid userId, string lang = "en")
    {
        var ctrl = new OnboardingWidgetController(_state.Object, _profile.Object);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, "test"));
        http.Request.Headers["Accept-Language"] = lang;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctrl;
    }

    [Fact]
    public async Task Names_Post_SavesProfile_AndRedirectsToShifts()
    {
        var userId = Guid.NewGuid();
        _profile.Setup(p => p.SaveProfileAsync(userId,
                It.Is<string>(d => d == "Burner1"),
                It.IsAny<ProfileSaveRequest>(),
                It.IsAny<string>(),
                default))
            .ReturnsAsync(Guid.NewGuid());

        var ctrl = BuildSut(userId);
        var vm = new NamesViewModel { BurnerName = "Burner1", FirstName = "First", LastName = "Last" };

        var result = await ctrl.Names(vm, default);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Shifts), redirect.ActionName);
        _profile.Verify(p => p.SaveProfileAsync(userId, "Burner1",
            It.Is<ProfileSaveRequest>(r => r.FirstName == "First" && r.LastName == "Last" && r.BurnerName == "Burner1"),
            It.IsAny<string>(),
            default),
            Times.Once);
    }

    [Fact]
    public async Task Names_Post_InvalidModel_ReturnsView()
    {
        var ctrl = BuildSut(Guid.NewGuid());
        ctrl.ModelState.AddModelError(nameof(NamesViewModel.BurnerName), "required");

        var result = await ctrl.Names(new NamesViewModel(), default);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Names), view.ViewName ?? nameof(OnboardingWidgetController.Names));
    }
}
```

- [ ] **Step 3: Run — expect failure (signature mismatch / not implemented)**

```bash
dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~OnboardingWidgetControllerNamesTests" -v quiet
```

- [ ] **Step 4: Implement the controller actions**

In `OnboardingWidgetController.cs`:

1. Update the constructor to inject `IProfileService _profileService`.
2. Replace the `Names()` stub with:

```csharp
[HttpGet]
public IActionResult Names()
{
    // Pre-fill from OAuth claims when present.
    var vm = new Humans.Web.Models.OnboardingWidget.NamesViewModel
    {
        FirstName = User.FindFirstValue(ClaimTypes.GivenName) ?? string.Empty,
        LastName = User.FindFirstValue(ClaimTypes.Surname) ?? string.Empty,
    };
    return View(vm);
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Names(Humans.Web.Models.OnboardingWidget.NamesViewModel vm, CancellationToken ct)
{
    if (!ModelState.IsValid)
        return View(vm);

    var userId = GetUserId();
    var language = HttpContext.Request.Headers["Accept-Language"].ToString().Split(',').FirstOrDefault() ?? "en";

    var request = new Humans.Application.Interfaces.Profiles.ProfileSaveRequest
    {
        BurnerName = vm.BurnerName,
        FirstName = vm.FirstName,
        LastName = vm.LastName,
    };

    await _profileService.SaveProfileAsync(userId, vm.BurnerName, request, language, ct);

    return RedirectToAction(nameof(Shifts));
}
```

3. Update the controller constructor dependencies. Add `IProfileService _profileService` and assign it.

If `ProfileSaveRequest` requires more fields than `BurnerName`/`FirstName`/`LastName`, look at `ProfileController.Edit` for the canonical population pattern and copy only those required-on-save fields, defaulting optionals to `null`.

- [ ] **Step 5: Create the view**

`src/Humans.Web/Views/OnboardingWidget/Names.cshtml`:

```cshtml
@model Humans.Web.Models.OnboardingWidget.NamesViewModel
@{
    ViewData["Title"] = "Welcome — let's start with your name";
}

<div class="row justify-content-center">
    <div class="col-md-8 col-lg-6">
        <h1 class="mb-3">Welcome — let's start with your name</h1>
        <p class="text-muted mb-4">Two short steps before you pick a shift.</p>

        <form asp-action="Names" method="post">
            @Html.AntiForgeryToken()

            <div class="mb-3">
                <label asp-for="FirstName" class="form-label"></label>
                <input asp-for="FirstName" class="form-control" autocomplete="given-name" />
                <span asp-validation-for="FirstName" class="text-danger"></span>
            </div>

            <div class="mb-3">
                <label asp-for="LastName" class="form-label"></label>
                <input asp-for="LastName" class="form-control" autocomplete="family-name" />
                <span asp-validation-for="LastName" class="text-danger"></span>
            </div>

            <div class="mb-4">
                <label asp-for="BurnerName" class="form-label"></label>
                <input asp-for="BurnerName" class="form-control" />
                <small class="form-text text-muted">The name everyone will know you by.</small>
                <span asp-validation-for="BurnerName" class="text-danger"></span>
            </div>

            <button type="submit" class="btn btn-primary btn-lg">Continue</button>
        </form>
    </div>
</div>
```

- [ ] **Step 6: Run tests — expect pass**

```bash
dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~OnboardingWidgetControllerNamesTests" -v quiet
```

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Models/OnboardingWidget/NamesViewModel.cs \
        src/Humans.Web/Views/OnboardingWidget/Names.cshtml \
        src/Humans.Web/Controllers/OnboardingWidgetController.cs \
        tests/Humans.Web.Tests/Controllers/OnboardingWidgetControllerNamesTests.cs
git commit -m "feat(onboarding): widget step 1 — names form"
```

---

## Task 4: `priorityOnly` filter on shift browse

**Files:**
- Modify: `src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs` — add `priorityOnly` parameter to whichever method backs the public browse view (search for the method called by `ShiftsController.Index`).
- Modify: `src/Humans.Application/Services/Shifts/ShiftManagementService.cs` — implementation.
- Test: append to `tests/Humans.Application.Tests/Services/Shifts/ShiftManagementServiceTests.cs` (or create one if missing).

**"Priority" definition:** `Rota.Priority` ∈ `{Important, Essential}` ∪ shifts where `confirmed_count < MinVolunteers`. Default `priorityOnly = false` so all existing callers behave identically.

- [ ] **Step 1: Locate the browse method**

```bash
grep -n "GetBrowseModel\|GetVolunteerBrowse\|BuildBrowse" src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs
```
Read the matched method's signature and adjacent doc.

- [ ] **Step 2: Failing test**

Add to `ShiftManagementServiceTests.cs`:

```csharp
[Fact]
public async Task Browse_PriorityOnly_FiltersToImportantOrEssentialOrUnderstaffed()
{
    // Arrange three rotas: Normal+staffed (excluded), Important (included),
    // Normal but understaffed (included). Use the existing test fixture
    // pattern in this file for seeding.
    // ... (mirror the existing "Browse" test arrangement, set priorityOnly: true)
    // Assert the model contains only the Important + understaffed rota/shift rows.
}
```

(Mirror the file's existing arrangement helpers; do not invent new seeding helpers.)

- [ ] **Step 3: Run — expect failure (parameter does not exist)**

```bash
dotnet test --filter "FullyQualifiedName~Browse_PriorityOnly_FiltersToImportantOrEssentialOrUnderstaffed" -v quiet
```

- [ ] **Step 4: Add the parameter + filter**

In `IShiftManagementService.cs`, append `bool priorityOnly = false` (with default to preserve existing callers) to the browse method signature.

In `ShiftManagementService.cs`, after the existing rota/shift fetch, if `priorityOnly` is true filter:

```csharp
if (priorityOnly)
{
    rotas = rotas.Where(r =>
        r.Priority == RotaPriority.Important ||
        r.Priority == RotaPriority.Essential ||
        r.Shifts.Any(s => s.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed) < s.MinVolunteers))
        .ToList();
}
```

(Adapt names to the actual entities; `RotaPriority` enum and `Shift.MinVolunteers` are confirmed in `docs/sections/Shifts.md`.)

- [ ] **Step 5: Run tests — expect pass + no regression**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~ShiftManagementServiceTests" -v quiet
```

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs \
        src/Humans.Application/Services/Shifts/ShiftManagementService.cs \
        tests/Humans.Application.Tests/Services/Shifts/ShiftManagementServiceTests.cs
git commit -m "feat(shifts): priorityOnly filter for browse"
```

---

## Task 5: Step 2 — Shifts (view + GET/POST/Skip)

**Files:**
- Create: `src/Humans.Web/Models/OnboardingWidget/ShiftsStepViewModel.cs`
- Create: `src/Humans.Web/Views/OnboardingWidget/Shifts.cshtml`
- Modify: `src/Humans.Web/Controllers/OnboardingWidgetController.cs` — implement `Shifts` GET, `SignUp` POST, `Skip` POST.
- Test: `tests/Humans.Web.Tests/Controllers/OnboardingWidgetControllerShiftsTests.cs`

**Behavior:**
- `Shifts` GET: call the browse method with `priorityOnly: true` by default; expose a `?showAll=true` query that toggles to the full browse.
- `SignUp` POST: takes `shiftId` (or `rotaId` + range), calls `IShiftSignupService.SignUpAsync` / `SignUpRangeAsync`, redirects to `Consents`.
- `Skip` POST: writes `OnboardingWidgetState.ShiftSkipSessionKey = "true"` to session, redirects to `Consents`.

- [ ] **Step 1: ViewModel**

`src/Humans.Web/Models/OnboardingWidget/ShiftsStepViewModel.cs`:

```csharp
namespace Humans.Web.Models.OnboardingWidget;

public class ShiftsStepViewModel
{
    public bool ShowAll { get; set; }
    /// <summary>The browse-partial model — populated from IShiftManagementService.</summary>
    public required object BrowseModel { get; set; }
}
```

(Replace `object` with the actual browse-model type the existing `_EventRotaTable` partial expects — find it via `grep -n "@model" src/Humans.Web/Views/Shifts/_EventRotaTable.cshtml`.)

- [ ] **Step 2: Failing tests**

```csharp
using System.Security.Claims;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Onboarding;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Humans.Web.Tests.Controllers;

public class OnboardingWidgetControllerShiftsTests
{
    private readonly Mock<IOnboardingWidgetState> _state = new();
    private readonly Mock<IProfileService> _profile = new();
    private readonly Mock<IShiftSignupService> _signups = new();
    private readonly Mock<IShiftManagementService> _shiftMgmt = new();
    private readonly DefaultHttpContext _http = new();

    private OnboardingWidgetController BuildSut(Guid userId)
    {
        _http.Session = new TestSession();
        _http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        }, "test"));
        var ctrl = new OnboardingWidgetController(_state.Object, _profile.Object, _signups.Object, _shiftMgmt.Object);
        ctrl.ControllerContext = new ControllerContext { HttpContext = _http };
        return ctrl;
    }

    [Fact]
    public async Task SignUp_Post_CallsService_AndRedirectsToConsents()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _signups.Setup(s => s.SignUpAsync(userId, shiftId, userId, false))
            .ReturnsAsync(SignupResult.Ok(new Humans.Domain.Entities.ShiftSignup()));
        var ctrl = BuildSut(userId);

        var result = await ctrl.SignUp(shiftId, default);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
    }

    [Fact]
    public async Task Skip_Post_SetsSessionFlag_AndRedirectsToConsents()
    {
        var userId = Guid.NewGuid();
        var ctrl = BuildSut(userId);

        var result = await ctrl.Skip(default);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
        Assert.Equal("true", _http.Session.GetString(OnboardingWidgetState.ShiftSkipSessionKey));
    }

    private sealed class TestSession : ISession { /* paste the helper from Task 1 */ }
}
```

- [ ] **Step 3: Run — expect failure**

```bash
dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~OnboardingWidgetControllerShiftsTests" -v quiet
```

- [ ] **Step 4: Implement controller actions**

Add to `OnboardingWidgetController` (and update constructor to inject `IShiftSignupService _signupService` and `IShiftManagementService _shiftMgmt`):

```csharp
[HttpGet]
public async Task<IActionResult> Shifts(bool showAll = false, CancellationToken ct = default)
{
    var browseModel = await _shiftMgmt.GetBrowseModelAsync(/* args... */, priorityOnly: !showAll);
    return View(new ShiftsStepViewModel { ShowAll = showAll, BrowseModel = browseModel });
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SignUp(Guid shiftId, CancellationToken ct)
{
    var userId = GetUserId();
    var result = await _signupService.SignUpAsync(userId, shiftId, userId, false);
    if (!result.Success)
    {
        TempData["Error"] = result.Error ?? "Could not sign up.";
        return RedirectToAction(nameof(Shifts));
    }
    return RedirectToAction(nameof(Consents));
}

[HttpPost]
[ValidateAntiForgeryToken]
public IActionResult Skip(CancellationToken ct)
{
    HttpContext.Session.SetString(
        Humans.Application.Services.Onboarding.OnboardingWidgetState.ShiftSkipSessionKey,
        "true");
    return RedirectToAction(nameof(Consents));
}
```

(Replace `GetBrowseModelAsync` and its arguments with the actual signature from Task 4.)

- [ ] **Step 5: Create the view**

`src/Humans.Web/Views/OnboardingWidget/Shifts.cshtml` — render the existing `_EventRotaTable` and `_BuildStrikeRotaTable` partials with the `Model.BrowseModel`. Add a "Show all shifts" toggle (`<a asp-action="Shifts" asp-route-showAll="true">`) when `!Model.ShowAll`. Add a "Not right now" form posting to `Skip` with localized nag copy ("ok but be sure to come back later — it's more fun when we build this all together"). Add an empty-state block that renders when no shifts are visible (mirrors today's `BrowsingClosed.cshtml`).

- [ ] **Step 6: Run tests — expect pass**

```bash
dotnet test tests/Humans.Web.Tests/Humans.Web.Tests.csproj --filter "FullyQualifiedName~OnboardingWidgetControllerShiftsTests" -v quiet
```

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Models/OnboardingWidget/ShiftsStepViewModel.cs \
        src/Humans.Web/Views/OnboardingWidget/Shifts.cshtml \
        src/Humans.Web/Controllers/OnboardingWidgetController.cs \
        tests/Humans.Web.Tests/Controllers/OnboardingWidgetControllerShiftsTests.cs
git commit -m "feat(onboarding): widget step 2 — priority shifts + skip"
```

---

## Task 6: Force-Pending branch in `ShiftSignupService.SignUpAsync` / `SignUpRangeAsync`

**Files:**
- Modify: `src/Humans.Application/Services/Shifts/ShiftSignupService.cs`
- Test: `tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServiceForcePendingTests.cs`

**Branch logic:** at the head of `SignUpAsync` (and `SignUpRangeAsync`), call `_membership.HasAllRequiredConsentsForTeamAsync(userId, SystemTeamIds.Volunteers, ct)`. If false, the resulting signup must remain `Pending` regardless of `Rota.Policy`. The existing path that auto-Confirms on Public rotas should be skipped for these users.

If `ShiftSignupService` does not already depend on `IMembershipCalculator`, inject it.

- [ ] **Step 1: Failing tests**

```csharp
using Humans.Application.Interfaces.Governance;
using Humans.Application.Services.Shifts;
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Moq;
using Xunit;

namespace Humans.Application.Tests.Services.Shifts;

public class ShiftSignupServiceForcePendingTests
{
    // Use the existing in-memory test setup pattern from this folder. Seed:
    //   - One Public rota with one Pending-capable shift.
    //   - One RequireApproval rota with one shift.

    [Fact]
    public async Task SignUp_PublicRota_UserMissingConsents_ReturnsPending()
    {
        var (svc, db) = BuildSut(userHasConsents: false);
        var (_, shift) = SeedPublicShift(db);

        var result = await svc.SignUpAsync(_userId, shift.Id, _userId);

        Assert.True(result.Success);
        Assert.Equal(SignupStatus.Pending, result.Signup!.Status);
    }

    [Fact]
    public async Task SignUp_PublicRota_UserWithConsents_ReturnsConfirmed()
    {
        var (svc, db) = BuildSut(userHasConsents: true);
        var (_, shift) = SeedPublicShift(db);

        var result = await svc.SignUpAsync(_userId, shift.Id, _userId);

        Assert.Equal(SignupStatus.Confirmed, result.Signup!.Status);
    }

    [Fact]
    public async Task SignUp_RequireApprovalRota_UserMissingConsents_StaysPending()
    {
        var (svc, db) = BuildSut(userHasConsents: false);
        var (_, shift) = SeedRequireApprovalShift(db);

        var result = await svc.SignUpAsync(_userId, shift.Id, _userId);

        Assert.Equal(SignupStatus.Pending, result.Signup!.Status);
    }

    [Fact]
    public async Task SignUpRange_PublicBuildRota_UserMissingConsents_AllBlockShiftsPending()
    {
        var (svc, db) = BuildSut(userHasConsents: false);
        var rota = SeedPublicBuildRotaWithShifts(db, dayOffsets: new[] { 0, 1, 2 });

        var result = await svc.SignUpRangeAsync(_userId, rota.Id, startDayOffset: 0, endDayOffset: 2, _userId);

        Assert.True(result.Success);
        var blockSignups = db.ShiftSignups.Where(s => s.UserId == _userId).ToList();
        Assert.All(blockSignups, s => Assert.Equal(SignupStatus.Pending, s.Status));
        Assert.True(blockSignups.All(s => s.SignupBlockId == blockSignups[0].SignupBlockId));
    }
}
```

(Fill in the concrete arrangement helpers by mirroring an existing test in the same folder.)

- [ ] **Step 2: Run — expect failure**

```bash
dotnet test --filter "FullyQualifiedName~ShiftSignupServiceForcePendingTests" -v quiet
```

- [ ] **Step 3: Add the branch in `SignUpAsync`**

Inject `IMembershipCalculator _membership` if not already present. At the start of `SignUpAsync` (before the existing rota-policy switch):

```csharp
var hasConsents = await _membership.HasAllRequiredConsentsForTeamAsync(
    userId, SystemTeamIds.Volunteers, default);
```

Then in the post-creation block where the entity is `Confirm()`-ed for Public rotas, gate the auto-confirm:

```csharp
// Pending is overloaded for users missing required Volunteer consents:
// the ConsentService promotion hook upgrades to Confirmed once admission fires.
// See: docs/superpowers/specs/2026-05-05-low-friction-shift-signup-design.md
if (rota.Policy == RotaPolicy.Public && hasConsents)
{
    signup.Confirm(/* existing args */);
}
```

(Read the existing code first; the exact existing call may use a system-reviewer constant. Preserve its arguments; only gate it on `hasConsents`.)

Apply the same pattern to `SignUpRangeAsync`.

- [ ] **Step 4: Run tests — expect pass + no regression**

```bash
dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj --filter "FullyQualifiedName~ShiftSignupService" -v quiet
```

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Shifts/ShiftSignupService.cs \
        tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServiceForcePendingTests.cs
git commit -m "feat(shifts): force Pending when user missing required consents

Public-rota auto-Confirm is gated on HasAllRequiredConsentsForTeam
(Volunteers) so mid-onboarding users do not become binding shift
commitments before they have signed legal docs. RequireApproval rotas
continue to be Pending as today."
```

---

## Task 7: Step 3 — Consents (view + GET/POST sign)

**Files:**
- Create: `src/Humans.Web/Models/OnboardingWidget/ConsentsStepViewModel.cs`
- Create: `src/Humans.Web/Views/OnboardingWidget/Consents.cshtml`
- Modify: `src/Humans.Web/Controllers/OnboardingWidgetController.cs` — implement `Consents` GET, `SignConsent` POST.
- Test: `tests/Humans.Web.Tests/Controllers/OnboardingWidgetControllerConsentsTests.cs`

**Behavior:**
- `Consents` GET: list each required Volunteer document with whether the user has signed it. Use `IConsentService` for the read.
- `SignConsent` POST: takes `documentVersionId`, calls `IConsentService.SubmitConsentAsync`, redirects back to `Consents` (or to `Finish` once all are signed; `IOnboardingWidgetState.GetCurrentStepAsync` will return `Complete` after the last consent lands).

- [ ] **Step 1: ViewModel**

```csharp
namespace Humans.Web.Models.OnboardingWidget;

public class ConsentsStepViewModel
{
    public required IReadOnlyList<RequiredConsentRow> RequiredConsents { get; init; }
}

public record RequiredConsentRow(Guid DocumentVersionId, string Title, string Url, bool Signed);
```

- [ ] **Step 2: Failing test (controller routes signing through `IConsentService`)**

```csharp
[Fact]
public async Task SignConsent_Post_CallsConsentService_AndRedirectsBackToConsents()
{
    var userId = Guid.NewGuid();
    var docVersionId = Guid.NewGuid();
    _consents.Setup(c => c.SubmitConsentAsync(userId, docVersionId, default))
        .ReturnsAsync(new ConsentSubmitResult { Success = true });
    var ctrl = BuildSut(userId);

    var result = await ctrl.SignConsent(docVersionId, default);

    var redirect = Assert.IsType<RedirectToActionResult>(result);
    Assert.Equal(nameof(OnboardingWidgetController.Consents), redirect.ActionName);
    _consents.Verify(c => c.SubmitConsentAsync(userId, docVersionId, default), Times.Once);
}
```

- [ ] **Step 3: Run — expect failure**

```bash
dotnet test --filter "FullyQualifiedName~OnboardingWidgetControllerConsentsTests" -v quiet
```

- [ ] **Step 4: Implement actions**

Inject `IConsentService _consents` into the controller. Add:

```csharp
[HttpGet]
public async Task<IActionResult> Consents(CancellationToken ct)
{
    var userId = GetUserId();
    var rows = await _consents.GetRequiredConsentRowsForUserAsync(userId, SystemTeamIds.Volunteers, ct);
    var vm = new ConsentsStepViewModel
    {
        RequiredConsents = rows.Select(r => new RequiredConsentRow(
            r.DocumentVersionId, r.Title, r.Url, r.Signed)).ToList(),
    };
    return View(vm);
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SignConsent(Guid documentVersionId, CancellationToken ct)
{
    var userId = GetUserId();
    var result = await _consents.SubmitConsentAsync(userId, documentVersionId, ct);
    if (!result.Success) TempData["Error"] = result.Error;
    return RedirectToAction(nameof(Consents));
}
```

If `IConsentService` does not have a `GetRequiredConsentRowsForUserAsync` method that returns the list with signed status, this method needs to be added — check the interface first, and either reuse an existing read or add the read method (pure read, no schema). If you have to add it, mirror the pattern of an existing list-read method.

- [ ] **Step 5: View**

`src/Humans.Web/Views/OnboardingWidget/Consents.cshtml`:

```cshtml
@model Humans.Web.Models.OnboardingWidget.ConsentsStepViewModel
@{ ViewData["Title"] = "One last step — sign the required documents"; }

<div class="row justify-content-center">
    <div class="col-md-8 col-lg-6">
        <h1 class="mb-3">One last step</h1>
        <p class="text-muted mb-4">Please sign the required volunteer documents.</p>

        @foreach (var row in Model.RequiredConsents)
        {
            <div class="card mb-3">
                <div class="card-body d-flex justify-content-between align-items-center">
                    <div>
                        <h5 class="mb-1">@row.Title</h5>
                        <a href="@row.Url" target="_blank">Read the document</a>
                    </div>
                    @if (row.Signed)
                    {
                        <span class="badge bg-success">Signed</span>
                    }
                    else
                    {
                        <form asp-action="SignConsent" method="post" class="m-0">
                            @Html.AntiForgeryToken()
                            <input type="hidden" name="documentVersionId" value="@row.DocumentVersionId" />
                            <button type="submit" class="btn btn-primary">I agree</button>
                        </form>
                    }
                </div>
            </div>
        }
    </div>
</div>
```

- [ ] **Step 6: Run tests — expect pass**

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Models/OnboardingWidget/ConsentsStepViewModel.cs \
        src/Humans.Web/Views/OnboardingWidget/Consents.cshtml \
        src/Humans.Web/Controllers/OnboardingWidgetController.cs \
        tests/Humans.Web.Tests/Controllers/OnboardingWidgetControllerConsentsTests.cs
git commit -m "feat(onboarding): widget step 3 — consents"
```

---

## Task 8: `PromoteWidgetPendingSignupsAfterAdmissionAsync` + ConsentService hook

**Files:**
- Modify: `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs` — add the new method.
- Modify: `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` — implement.
- Modify: `src/Humans.Application/Services/Consent/ConsentService.cs` (around line 224, after `SyncVolunteersMembershipForUserAsync`) — call the promotion hook.
- Test: `tests/Humans.Application.Tests/Services/Shifts/PromoteWidgetPendingSignupsAfterAdmissionTests.cs`
- Test: `tests/Humans.Application.Tests/Services/Consent/ConsentServiceTests.cs` (add a test for the hook call sequence).

- [ ] **Step 1: Add interface method**

```csharp
/// <summary>
/// After Volunteers admission lands for a user, promote their current-event
/// Pending signups: Public-rota signups → Confirmed; RequireApproval-rota
/// signups stay Pending awaiting coordinator. Range blocks promote together.
/// No-op when the user has no Pending signups.
/// </summary>
Task PromoteWidgetPendingSignupsAfterAdmissionAsync(Guid userId, CancellationToken ct = default);
```

- [ ] **Step 2: Failing tests**

```csharp
[Fact]
public async Task Promote_PublicRotaPendingSignup_BecomesConfirmed()
{
    var (svc, db) = BuildSut();
    var signup = SeedPendingSignup(db, RotaPolicy.Public, capacity: 5, confirmedSoFar: 0);

    await svc.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);

    var reloaded = db.ShiftSignups.Find(signup.Id);
    Assert.Equal(SignupStatus.Confirmed, reloaded!.Status);
}

[Fact]
public async Task Promote_RequireApprovalPendingSignup_StaysPending()
{
    var (svc, db) = BuildSut();
    var signup = SeedPendingSignup(db, RotaPolicy.RequireApproval, capacity: 5, confirmedSoFar: 0);

    await svc.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);

    Assert.Equal(SignupStatus.Pending, db.ShiftSignups.Find(signup.Id)!.Status);
}

[Fact]
public async Task Promote_PublicBuildRangeBlock_AllShiftsConfirmed()
{
    var (svc, db) = BuildSut();
    var blockId = Guid.NewGuid();
    SeedPendingBlock(db, blockId, RotaPolicy.Public, dayOffsets: new[] { 0, 1, 2 });

    await svc.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);

    var block = db.ShiftSignups.Where(s => s.SignupBlockId == blockId).ToList();
    Assert.All(block, s => Assert.Equal(SignupStatus.Confirmed, s.Status));
}

[Fact]
public async Task Promote_PublicShiftFilledSinceCreation_StaysPending()
{
    var (svc, db) = BuildSut();
    // Capacity 1, one Confirmed already → user's Pending cannot promote.
    var signup = SeedPendingSignup(db, RotaPolicy.Public, capacity: 1, confirmedSoFar: 1);

    await svc.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);

    Assert.Equal(SignupStatus.Pending, db.ShiftSignups.Find(signup.Id)!.Status);
}

[Fact]
public async Task Promote_NoPendingSignups_DoesNotThrow()
{
    var (svc, _) = BuildSut();

    await svc.PromoteWidgetPendingSignupsAfterAdmissionAsync(_userId);
}
```

(Mirror the seeding pattern in existing `ShiftSignupService` tests.)

- [ ] **Step 3: Run — expect failure**

```bash
dotnet test --filter "FullyQualifiedName~PromoteWidgetPendingSignups" -v quiet
```

- [ ] **Step 4: Implement**

In `ShiftSignupService.cs`:

```csharp
public async Task PromoteWidgetPendingSignupsAfterAdmissionAsync(
    Guid userId, CancellationToken ct = default)
{
    var activeEvent = await _shiftMgmt.GetActiveAsync();
    if (activeEvent is null) return;

    var (shiftIds, statuses) = await GetActiveSignupStatusesAsync(userId, activeEvent.Id);
    var pendingShiftIds = statuses
        .Where(kvp => kvp.Value == SignupStatus.Pending)
        .Select(kvp => kvp.Key)
        .ToList();
    if (pendingShiftIds.Count == 0) return;

    // Load Pending signups with Shift.Rota for policy + capacity checks.
    var signups = (await GetByUserAsync(userId, activeEvent.Id))
        .Where(s => s.Status == SignupStatus.Pending)
        .ToList();

    foreach (var signup in signups)
    {
        // Public + capacity available → Confirm.
        // RequireApproval → leave Pending (existing semantics).
        // If shift filled since creation → leave Pending; coordinator can refuse.
        if (signup.Shift.Rota.Policy != RotaPolicy.Public) continue;

        var confirmed = signup.Shift.ShiftSignups.Count(ss => ss.Status == SignupStatus.Confirmed);
        if (confirmed >= signup.Shift.MaxVolunteers) continue;

        signup.Confirm(/* existing system-reviewer args */);
    }

    await _dbContext.SaveChangesAsync(ct);
}
```

(Adjust to actual repository/dbcontext access pattern in the file.)

- [ ] **Step 5: Hook from `ConsentService.SubmitConsentAsync`**

In `ConsentService.cs`, after the existing `await _syncJob.SyncVolunteersMembershipForUserAsync(userId);` line:

```csharp
await _shiftSignupService.PromoteWidgetPendingSignupsAfterAdmissionAsync(userId, default);
```

Inject `IShiftSignupService _shiftSignupService` into `ConsentService`'s constructor if not already present.

- [ ] **Step 6: Add ConsentService hook test**

Verify, with mocks, that `PromoteWidgetPendingSignupsAfterAdmissionAsync` is called once after `SyncVolunteersMembershipForUserAsync`:

```csharp
[Fact]
public async Task SubmitConsent_AfterAdmission_CallsPromoteHook()
{
    // Arrange: SubmitConsentAsync setup such that SyncVolunteersMembershipForUserAsync is called.
    // Act: SubmitConsentAsync.
    // Assert: _shiftSignupService.Verify(s => s.PromoteWidgetPendingSignupsAfterAdmissionAsync(...), Times.Once).
}
```

- [ ] **Step 7: Run all changed tests**

```bash
dotnet test tests/Humans.Application.Tests -v quiet
```

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs \
        src/Humans.Application/Services/Shifts/ShiftSignupService.cs \
        src/Humans.Application/Services/Consent/ConsentService.cs \
        tests/Humans.Application.Tests/Services/Shifts/PromoteWidgetPendingSignupsAfterAdmissionTests.cs \
        tests/Humans.Application.Tests/Services/Consent/ConsentServiceTests.cs
git commit -m "feat(shifts): promote widget Pending signups after admission

When ConsentService.SubmitConsentAsync admits a user to Volunteers,
their Pending current-event signups are promoted: Public-rota signups
to Confirmed, RequireApproval-rota signups stay Pending awaiting
coordinator. Range blocks promote together. Capacity is re-checked at
promotion time."
```

---

## Task 9: Home/Guest redirect-into-widget

**Files:**
- Modify: `src/Humans.Web/Controllers/HomeController.cs` — at the top of `IndexCore`, call `IOnboardingWidgetState`.
- Modify: `src/Humans.Web/Controllers/GuestController.cs` — at the top of `Index`, call `IOnboardingWidgetState`.
- Test: existing `HomeController` / `GuestController` tests; add new redirect cases.

- [ ] **Step 1: Failing test**

```csharp
[Fact]
public async Task Home_Index_RedirectsToOnboardingWidget_WhenNotComplete()
{
    // Authenticated user; IOnboardingWidgetState returns Names.
    // Expect RedirectToAction("Index", "OnboardingWidget").
}

[Fact]
public async Task Home_Index_PassesThrough_WhenComplete()
{
    // Authenticated active user; IOnboardingWidgetState returns Complete.
    // Expect existing dashboard view.
}
```

(Mirror Guest with a parallel pair.)

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Inject + redirect in HomeController**

In `HomeController` constructor, add `IOnboardingWidgetState _widgetState` and assign. At the very top of `IndexCore` (after the authenticated check), insert:

```csharp
var step = await _widgetState.GetCurrentStepAsync(user.Id, cancellationToken);
if (step != OnboardingWidgetStep.Complete)
{
    return RedirectToAction("Index", "OnboardingWidget");
}
```

Place it **after** `var user = await GetCurrentUserAsync();` so we have a userId. Remove or keep the existing `if (!hasProfile)` redirect to Guest — the widget redirect now covers profileless users; the Guest branch is dead for our flow but we leave it since `GuestController.Index` itself does the same widget check (a profileless authenticated user lands on Home → widget redirect → no Guest hop needed; if they navigate to /Guest directly, that controller does the same redirect).

- [ ] **Step 4: Inject + redirect in GuestController**

Same pattern at the top of `GuestController.Index`.

- [ ] **Step 5: Run tests — expect pass**

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Controllers/HomeController.cs \
        src/Humans.Web/Controllers/GuestController.cs \
        tests/Humans.Web.Tests/Controllers/HomeControllerTests.cs \
        tests/Humans.Web.Tests/Controllers/GuestControllerTests.cs
git commit -m "feat(onboarding): Home/Guest redirect into widget when incomplete"
```

---

## Task 10: `/Welcome` integration

**Files:**
- Modify: `src/Humans.Web/Controllers/WelcomeController.cs`
- Modify: `src/Humans.Web/Views/Welcome/Index.cshtml`
- Test: `tests/Humans.Web.Tests/Controllers/WelcomeControllerTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
[Fact]
public void Welcome_AnonymousVisitor_ReturnsView() { /* unchanged */ }

[Fact]
public void Welcome_ActiveMember_RedirectsToShifts() { /* unchanged */ }

[Fact]
public void Welcome_AuthenticatedNonActive_RedirectsToOnboardingWidget()
{
    // Authenticated, no ActiveMember claim.
    // Expect Redirect("/OnboardingWidget").
}
```

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Update controller**

In `WelcomeController.Index`, after the "active member → /Shifts" redirect, add:

```csharp
// Authenticated but not active — send them into the widget instead of re-rendering the explainer.
return Redirect("/OnboardingWidget");
```

The remaining `return View()` only runs for anonymous visitors.

- [ ] **Step 4: Update the view returnUrl**

In `Welcome/Index.cshtml`, change:

```cshtml
var shiftsUrl = Url.Action("Index", "Shifts");
var loginUrl = Url.Action("Login", "Account", new { returnUrl = shiftsUrl });
```

to:

```cshtml
var widgetUrl = Url.Action("Index", "OnboardingWidget");
var loginUrl = Url.Action("Login", "Account", new { returnUrl = widgetUrl });
```

- [ ] **Step 5: Run tests — expect pass + no regression on existing Welcome tests**

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/Controllers/WelcomeController.cs \
        src/Humans.Web/Views/Welcome/Index.cshtml \
        tests/Humans.Web.Tests/Controllers/WelcomeControllerTests.cs
git commit -m "feat(onboarding): /Welcome routes non-active visitors into widget"
```

---

## Task 11: Site-wide banner partial

**Files:**
- Create: `src/Humans.Web/Views/Shared/_OnboardingProgressBanner.cshtml`
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml`
- Modify: localized resources (`SharedResource.resx` and locale variants) — add 2 strings: `OnboardingBanner_Text`, `OnboardingBanner_Cta`.
- Test: `tests/Humans.Web.Tests/Views/OnboardingProgressBannerTests.cs` (or rely on integration tests if no view-test infrastructure exists).

- [ ] **Step 1: Partial**

```cshtml
@using Humans.Application.Interfaces.Onboarding
@inject IOnboardingWidgetState WidgetState
@inject IHtmlLocalizer<SharedResource> Localizer
@{
    if (User?.Identity?.IsAuthenticated != true) { return; }
    var userIdRaw = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (!Guid.TryParse(userIdRaw, out var userId)) { return; }
    var step = await WidgetState.GetCurrentStepAsync(userId);
    if (step == OnboardingWidgetStep.Complete) { return; }
}
<div class="alert alert-info mb-0 rounded-0 text-center">
    @Localizer["OnboardingBanner_Text"]
    <a href="@Url.Action("Index", "OnboardingWidget")" class="alert-link ms-2">
        @Localizer["OnboardingBanner_Cta"]
    </a>
</div>
```

- [ ] **Step 2: Wire into `_Layout.cshtml`**

Find the existing main `<body>` content area; just inside the navbar/header block insert:

```cshtml
<partial name="_OnboardingProgressBanner" />
```

- [ ] **Step 3: Add localized strings**

In `src/Humans.Web/Resources/SharedResource.resx` add entries:

```xml
<data name="OnboardingBanner_Text" xml:space="preserve">
  <value>Finish setting up your account to access shifts and team features.</value>
</data>
<data name="OnboardingBanner_Cta" xml:space="preserve">
  <value>Continue setup →</value>
</data>
```

Add equivalent entries to all five locale variants (`SharedResource.es.resx`, `.ca.resx`, `.de.resx`, `.fr.resx`, `.it.resx`). For non-English variants, use placeholder English text on first commit and flag for translation in the PR description.

- [ ] **Step 4: Build + smoke**

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
# Then open / in a browser logged in as a mid-widget user — banner should render.
```

(Skip the `dotnet run` if running in an automation context; the build pass is the gate.)

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/Shared/_OnboardingProgressBanner.cshtml \
        src/Humans.Web/Views/Shared/_Layout.cshtml \
        src/Humans.Web/Resources/SharedResource.resx \
        src/Humans.Web/Resources/SharedResource.*.resx
git commit -m "feat(onboarding): site-wide widget banner"
```

---

## Task 12: Profile-completion percent on Home

**Files:**
- Modify: `src/Humans.Web/Models/DashboardViewModel.cs` (or wherever `IndexCore` builds its VM) — add `ProfileCompletionPercent` (int, 0–100).
- Modify: `src/Humans.Web/Controllers/HomeController.cs` — compute the percentage in `IndexCore`.
- Modify: `src/Humans.Web/Views/Home/Dashboard.cshtml` (the existing dashboard view) — render a "Your profile is N% complete" indicator with a link to `Profile/Edit` when `< 100`.
- Test: `tests/Humans.Web.Tests/Helpers/ProfileCompletionTests.cs` — pure-function test of the percentage.

- [ ] **Step 1: Pure helper + failing test**

`src/Humans.Application/Helpers/ProfileCompletion.cs` (or in `Humans.Web` if app-layer is too strict — read existing helpers):

```csharp
using Humans.Domain.Entities;

namespace Humans.Application.Helpers;

public static class ProfileCompletion
{
    /// <summary>
    /// Percentage of optional Profile fields that are populated. Required
    /// fields (BurnerName, FirstName, LastName) are excluded — by definition
    /// any user reaching the Home dashboard has them.
    /// </summary>
    public static int ComputePercent(Profile profile)
    {
        if (profile is null) return 0;

        var checks = new bool[]
        {
            !string.IsNullOrEmpty(profile.City) && !string.IsNullOrEmpty(profile.CountryCode),
            !string.IsNullOrEmpty(profile.Bio),
            !string.IsNullOrEmpty(profile.Pronouns),
            !string.IsNullOrEmpty(profile.ContributionInterests),
            profile.BirthdayMonth.HasValue && profile.BirthdayDay.HasValue,
            !string.IsNullOrEmpty(profile.EmergencyContactName),
            !string.IsNullOrEmpty(profile.EmergencyContactPhone),
            !string.IsNullOrEmpty(profile.EmergencyContactRelationship),
        };

        var filled = checks.Count(c => c);
        return (int)Math.Round(100.0 * filled / checks.Length);
    }
}
```

(Confirm the actual property names from `Profile.cs` — adjust if mismatched.)

Test:

```csharp
[Fact]
public void EmptyProfile_Returns0()
{
    Assert.Equal(0, ProfileCompletion.ComputePercent(new Profile { BurnerName = "x", FirstName = "y", LastName = "z" }));
}

[Fact]
public void AllFieldsFilled_Returns100()
{
    var p = new Profile
    {
        BurnerName = "x", FirstName = "y", LastName = "z",
        City = "Madrid", CountryCode = "ES",
        Bio = "...", Pronouns = "they",
        ContributionInterests = "art",
        BirthdayMonth = 3, BirthdayDay = 15,
        EmergencyContactName = "n", EmergencyContactPhone = "p", EmergencyContactRelationship = "r",
    };
    Assert.Equal(100, ProfileCompletion.ComputePercent(p));
}
```

- [ ] **Step 2: Run — expect failure**

- [ ] **Step 3: Add to `DashboardViewModel`**

Add `public int ProfileCompletionPercent { get; init; }` to the existing VM record/class. Set it in `IndexCore` after the existing `data.Profile` access:

```csharp
ProfileCompletionPercent = data.Profile is not null
    ? ProfileCompletion.ComputePercent(data.Profile)
    : 0,
```

- [ ] **Step 4: Render in view**

In `Dashboard.cshtml`, add a small card / progress bar visible when `Model.ProfileCompletionPercent < 100`:

```cshtml
@if (Model.ProfileCompletionPercent < 100)
{
    <div class="card mb-3">
        <div class="card-body">
            <div class="d-flex justify-content-between align-items-center mb-2">
                <strong>Your profile is @Model.ProfileCompletionPercent% complete</strong>
                <a asp-controller="Profile" asp-action="Edit" class="btn btn-sm btn-outline-primary">Finish profile</a>
            </div>
            <div class="progress" style="height: 8px;">
                <div class="progress-bar" role="progressbar"
                     style="width: @(Model.ProfileCompletionPercent)%"></div>
            </div>
        </div>
    </div>
}
```

- [ ] **Step 5: Build + run helper test**

```bash
dotnet test --filter "FullyQualifiedName~ProfileCompletionTests" -v quiet
```

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Helpers/ProfileCompletion.cs \
        src/Humans.Web/Models/DashboardViewModel.cs \
        src/Humans.Web/Controllers/HomeController.cs \
        src/Humans.Web/Views/Home/Dashboard.cshtml \
        tests/Humans.Web.Tests/Helpers/ProfileCompletionTests.cs
git commit -m "feat(home): profile completion percent indicator"
```

---

## Task 13: Coordinator "Incomplete onboarding" filter on Pending list

**Files:**
- Locate the existing coordinator-side Pending-signup list. Likely candidates: `src/Humans.Web/Controllers/ShiftAdminController.cs`, `src/Humans.Web/Controllers/ShiftDashboardController.cs`, or a `Teams/{slug}/Shifts` partial. Run:

```bash
grep -rn "Pending" src/Humans.Web/Controllers/ShiftAdminController.cs src/Humans.Web/Controllers/ShiftDashboardController.cs 2>/dev/null | head -20
```

Identify which controller renders the Pending list. Modify only that one and its view + VM.

- Modify: that controller + view + VM.
- Modify: corresponding service method (`IShiftSignupService` / `IShiftManagementService`) to accept a `bool incompleteOnboardingOnly` filter; implement by joining (in-memory or via a repository read) the user's Volunteer-consent status.
- Test: append to the existing tests for that controller / service.

**Filter semantic:** "Volunteer is missing required consents" → the same `IMembershipCalculator.HasAllRequiredConsentsForTeamAsync` check used elsewhere.

- [ ] **Step 1: Identify the existing Pending list endpoint and add a failing test**

Add a test to the existing controller-tests file asserting that when `?incompleteOnboarding=true` is passed, the result excludes signups whose users have all required consents.

- [ ] **Step 2: Implementation**

Add the parameter to the controller action and to the service read it calls. The service-side filter:

```csharp
// Pseudocode — adapt to actual repository pattern.
if (incompleteOnboardingOnly)
{
    var userIds = signups.Select(s => s.UserId).Distinct().ToList();
    var withConsents = await _membership.GetUsersWithAllRequiredConsentsForTeamAsync(
        userIds, SystemTeamIds.Volunteers, ct);
    signups = signups.Where(s => !withConsents.Contains(s.UserId)).ToList();
}
```

(Use a batch read if the existing `IMembershipCalculator` exposes one — `GetUsersWithAllRequiredConsentsForTeamAsync` (historical; current behavior documented in `docs/features/governance/membership-status.md`, Consent Check section). If not, fall back to `HasAllRequiredConsentsForTeamAsync` per-user.)

- [ ] **Step 3: View — add the filter chip**

In the Pending list view, add a checkbox/toggle that posts `?incompleteOnboarding=true` and re-renders.

- [ ] **Step 4: Run tests, commit**

```bash
git add /* the modified files */
git commit -m "feat(shifts): coordinator filter for incomplete-onboarding Pending signups"
```

---

## Task 14: Documentation updates

**Files:**
- Modify: `docs/sections/Onboarding.md` — add to invariants: "Onboarding can be completed via the legacy linear flow (Profile → Consents) or the `/OnboardingWidget` guided flow (Names → Shifts → Consents). The data and admission rules are identical; the widget reorders the user-facing screens. Active-member admission still fires from `ConsentService.SubmitConsentAsync`'s `SyncVolunteersMembershipForUserAsync` call." Update the negative-access-rules section to remove the "profileless accounts cannot access Shifts" line *for the widget's Step 2 only* — note that profileless mid-widget users see the priority-shift list inside the widget, while direct navigation to `/Shifts` still routes them through the membership filter as today.
- Modify: `docs/sections/Shifts.md` — add an invariant: "Pending status on a Public rota indicates either (a) a coordinator-approval-required rota, or (b) a mid-widget volunteer whose required Volunteer consents have not landed yet. Case (b) auto-promotes to Confirmed when consents complete via `IShiftSignupService.PromoteWidgetPendingSignupsAfterAdmissionAsync`, called from `ConsentService.SubmitConsentAsync`."
- Modify: `docs/features/24-ticket-vendor-integration.md` — update the `/Welcome` section to note that the sign-in returnUrl now points at `/OnboardingWidget` and that authenticated non-active visitors are redirected into the widget rather than the explainer.
- Optional: add a memory atom under `memory/architecture/` if the force-Pending-on-missing-consents behavior is a new architectural rule. Decision: **add** — `memory/architecture/widget-pending-promotion.md`. Pattern + format spec lives in `memory/META.md`.

- [ ] **Step 1: Edit each doc.** Use `Edit` tool with the exact phrasings above; keep each amendment to ≤3 sentences.

- [ ] **Step 2: Add `memory/INDEX.md` entry**

Append:

```markdown
- [Widget Pending → Confirmed promotion](architecture/widget-pending-promotion.md) — how mid-onboarding signups stay Pending until consents land
```

- [ ] **Step 3: Build (docs are not built, but a smoke build catches accidental code edits)**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 4: Commit**

```bash
git add docs/sections/Onboarding.md \
        docs/sections/Shifts.md \
        docs/features/24-ticket-vendor-integration.md \
        memory/architecture/widget-pending-promotion.md \
        memory/INDEX.md
git commit -m "docs: invariant amendments + memory atom for widget flow"
```

---

## Final: full-suite test + push

- [ ] **Step 1: Full build + test pass**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx -v quiet
```

If anything fails, fix at the source (do not skip / mark inconclusive).

- [ ] **Step 2: Push**

```bash
git push
```

- [ ] **Step 3: Open a draft PR**

```bash
gh pr create --draft --base main --title "feat(onboarding): low-friction shift signup widget" --body "$(cat <<'EOF'
## Summary
- New `/OnboardingWidget` (Names → Shifts → Consents) entered via `/Welcome`
- Mid-onboarding shift signups force `Pending` until consents land, then auto-promote
- Site-wide "finish setup" banner; profile-completion percent on Home dashboard
- Coordinator "Incomplete onboarding" filter on the Pending list

Spec: `docs/superpowers/specs/2026-05-05-low-friction-shift-signup-design.md`
Plan: `docs/superpowers/plans/2026-05-05-low-friction-shift-signup.md`

## Test plan
- [ ] New OAuth signup → land on `/OnboardingWidget/Names` → submit → Step 2 → pick → Step 3 → land on Home
- [ ] "Not right now" on Step 2 → Step 3 → become active member without a signup
- [ ] Bail mid-widget, return: routed back to the right step via dispatcher
- [ ] Active member visits `/Welcome` → still redirected to `/Shifts`
- [ ] Coordinator sees Pending signup with "Incomplete onboarding" filter; filter scopes correctly
- [ ] Public-rota auto-confirm preserved for users who already have consents
- [ ] Last-consent submission promotes Pending Public-rota signup to Confirmed

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

(Mark as draft so QA can deploy the preview environment and the bot reviewers can run before merge.)

---

## Self-review checklist

After executing all tasks, verify against the spec:

- [ ] `IOnboardingWidgetState` returns `Complete` first when consents are signed (Task 1).
- [ ] Dispatcher exists and routes per state (Task 2).
- [ ] Names step pre-fills from OAuth (Task 3).
- [ ] Priority filter excludes Normal+staffed rotas (Task 4).
- [ ] Skip writes session flag and advances to Consents (Task 5).
- [ ] Force-Pending fires only when user lacks required consents (Task 6).
- [ ] Consents step delegates signing to existing `ConsentService` (Task 7).
- [ ] Promotion hook runs after `SyncVolunteersMembershipForUserAsync` (Task 8).
- [ ] Home and Guest both redirect into widget when not Complete (Task 9).
- [ ] `/Welcome` view's returnUrl is `/OnboardingWidget`; non-active authenticated visitors redirect there (Task 10).
- [ ] Banner renders only when state is not `Complete` (Task 11).
- [ ] Profile-completion percent reflects optional fields, not required ones (Task 12).
- [ ] Coordinator filter scopes to users missing required Volunteer consents (Task 13).
- [ ] Doc amendments + memory atom in place (Task 14).
- [ ] No new database migration created.
- [ ] `MembershipRequiredFilter.cs` is unchanged (`git diff origin/main -- src/Humans.Web/Authorization/MembershipRequiredFilter.cs` → empty).
