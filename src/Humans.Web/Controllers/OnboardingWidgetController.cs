using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Models.OnboardingWidget;
using Humans.Web.Services.Onboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Web.Controllers;

/// <summary>
/// Guided onboarding widget — three steps (Names → Shifts → Consents).
/// Index is the canonical dispatcher; /Welcome, Home/Index, Guest/Index, and the
/// layout banner all link here without needing to know which step a user is on.
/// </summary>
[Authorize]
public class OnboardingWidgetController : HumansControllerBase
{
    private readonly IOnboardingWidgetState _state;
    private readonly IProfileService _profileService;
    private readonly IShiftSignupService _signupService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IConsentService _consents;
    private readonly IUserService _userService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public OnboardingWidgetController(
        UserManager<User> userManager,
        IOnboardingWidgetState state,
        IProfileService profileService,
        IShiftSignupService signupService,
        IShiftManagementService shiftMgmt,
        IConsentService consents,
        IUserService userService,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager)
    {
        _state = state;
        _profileService = profileService;
        _signupService = signupService;
        _shiftMgmt = shiftMgmt;
        _consents = consents;
        _userService = userService;
        _localizer = localizer;
    }

    // [Authorize] guarantees the NameIdentifier claim is present.
    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var step = await _state.GetCurrentStepAsync(CurrentUserId(), ct);
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
    public IActionResult Names()
    {
        // Pre-fill from OAuth claims when present.
        var vm = new NamesViewModel
        {
            FirstName = User.FindFirstValue(ClaimTypes.GivenName) ?? string.Empty,
            LastName = User.FindFirstValue(ClaimTypes.Surname) ?? string.Empty,
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Names(NamesViewModel vm, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View(vm);

        var userId = CurrentUserId();

        // Guard: this endpoint is reachable directly. ProfileService.SaveProfileAsync
        // does a full-field overwrite, and the request below leaves most fields null.
        // Past Names step → dispatch onward instead of wiping already-populated data.
        var currentStep = await _state.GetCurrentStepAsync(userId, ct);
        if (currentStep != OnboardingWidgetStep.Names)
            return RedirectToAction(nameof(Index));

        var acceptLang = HttpContext.Request.Headers["Accept-Language"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var language = string.IsNullOrEmpty(acceptLang) ? "en" : acceptLang;

        var request = new ProfileSaveRequest(
            BurnerName: vm.BurnerName,
            FirstName: vm.FirstName,
            LastName: vm.LastName,
            City: null, CountryCode: null, Latitude: null, Longitude: null, PlaceId: null,
            Bio: null, Pronouns: null, ContributionInterests: null, BoardNotes: null,
            BirthdayMonth: null, BirthdayDay: null,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelationship: null,
            NoPriorBurnExperience: false,
            ProfilePictureData: null, ProfilePictureContentType: null, RemoveProfilePicture: false);

        await _profileService.SaveProfileAsync(userId, vm.BurnerName, request, language, ct);

        return RedirectToAction(nameof(Shifts));
    }

    [HttpGet]
    public async Task<IActionResult> Shifts(string? priority = null, CancellationToken ct = default)
    {
        var es = await _shiftMgmt.GetActiveAsync();
        if (es is null)
            return View(OnboardingShiftsBrowseModelBuilder.BuildEmpty(priority ?? string.Empty));

        // Stats line ("X% of critical filled, Y important open") needs the full
        // event-wide set, so we fetch with priorityOnly: false and let the
        // builder filter for display.
        var urgentShifts = await _shiftMgmt.GetBrowseShiftsAsync(
            es.Id, includeAdminOnly: false, includeSignups: true,
            includeHidden: false, priorityOnly: false);
        var (shiftIds, statuses) = await _signupService.GetActiveSignupStatusesAsync(CurrentUserId(), es.Id);
        var vm = OnboardingShiftsBrowseModelBuilder.Build(
            es, urgentShifts, shiftIds, statuses, priority ?? string.Empty);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(Guid shiftId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        var result = await _signupService.SignUpAsync(userId, shiftId, userId, false);
        if (!result.Success)
        {
            SetError(result.Error ?? "Could not sign up.");
            return RedirectToAction(nameof(Shifts));
        }
        return RedirectToAction(nameof(Consents));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUpRange(Guid rotaId, int startDayOffset, int endDayOffset, CancellationToken ct)
    {
        // Multi-day signup for Build/Strike rotas. Mirrors ShiftsController's
        // SignUpRange but routes back through the widget dispatcher so the
        // user lands on Consents (or Home, if all consents are signed)
        // instead of /Shifts/Index.
        var result = await _signupService.SignUpRangeAsync(CurrentUserId(), rotaId, startDayOffset, endDayOffset, isPrivileged: false);
        if (!result.Success)
        {
            SetError(result.Error ?? "Could not sign up for date range.");
            return RedirectToAction(nameof(Shifts));
        }
        return RedirectToAction(nameof(Consents));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Skip(CancellationToken ct)
    {
        HttpContext.Session.SetString(HttpOnboardingWidgetSessionState.ShiftSkipSessionKey, "true");
        return RedirectToAction(nameof(Consents));
    }

    [HttpGet]
    public async Task<IActionResult> Consents(CancellationToken ct)
    {
        var userId = CurrentUserId();

        // Stub-profile users can't sign consents (defense-in-depth gate in
        // ConsentService.SubmitConsentAsync would refuse). Bounce them back to
        // the Names step where they belong instead of rendering a doomed form.
        if (await IsStubAsync(userId, ct))
            return RedirectToNamesForStub();

        var rows = await _consents.GetRequiredConsentRowsForUserAsync(userId, SystemTeamIds.Volunteers, ct);
        var unsigned = rows.Where(r => !r.Signed).ToList();
        if (unsigned.Count == 0)
            return RedirectToAction(nameof(Index));

        var next = unsigned[0];
        var detail = await _consents.GetConsentReviewDetailAsync(next.DocumentVersionId, userId, ct);
        if (detail is null)
            return RedirectToAction(nameof(Index));

        var totalRequired = rows.Count;
        var currentIndex = totalRequired - unsigned.Count + 1;

        var vm = new ConsentsStepViewModel
        {
            DocumentVersionId = detail.DocumentVersionId,
            DocumentName = detail.DocumentName,
            VersionNumber = detail.VersionNumber,
            Content = new Dictionary<string, string>(detail.Content, StringComparer.Ordinal),
            ChangesSummary = detail.ChangesSummary,
            CurrentIndex = currentIndex,
            TotalRequired = totalRequired,
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignConsent(Guid documentVersionId, bool explicitConsent, CancellationToken ct)
    {
        if (!explicitConsent)
        {
            SetError(_localizer["Consent_MustCheck"].Value);
            return RedirectToAction(nameof(Consents));
        }

        var userId = CurrentUserId();

        // Mirror the GET-side gate so a Stub user who reaches the form via a
        // stale page or back-button can't POST into the StubProfile refusal
        // path below.
        if (await IsStubAsync(userId, ct))
            return RedirectToNamesForStub();

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _consents.SubmitConsentAsync(
            userId, documentVersionId, explicitConsent: true, ipAddress, userAgent, ct);

        if (!result.Success)
        {
            // Translate known error keys; never display the raw key to the user.
            switch (result.ErrorKey)
            {
                case "StubProfile":
                    // Defense-in-depth: gates above should have caught this.
                    return RedirectToNamesForStub();
                case "AlreadyConsented":
                    SetInfo(_localizer["Consent_AlreadyConsented"].Value);
                    break;
            }
        }

        // Always go through the dispatcher: routes Home once the final required
        // consent is signed instead of stranding the user on the signed-documents
        // view. Failure path also dispatches; TempData carries the message.
        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> IsStubAsync(Guid userId, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(userId, ct);
        return info is null || info.IsStub;
    }

    private IActionResult RedirectToNamesForStub()
    {
        SetInfo(_localizer["Consent_StubProfile_AddName"].Value);
        return RedirectToAction(nameof(Names));
    }
}
