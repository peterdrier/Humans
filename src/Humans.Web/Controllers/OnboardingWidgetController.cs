using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Web.Models.OnboardingWidget;
using Humans.Web.Services.Onboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Web.Controllers;

/// <summary>
/// Guided onboarding widget — three steps (Names → Shifts → Consents).
/// Index is the canonical dispatcher; /Welcome, Home/Index, Guest/Index, and the
/// layout banner all link here without needing to know which step a user is on.
/// </summary>
[Authorize]
public class OnboardingWidgetController(
    IUserService userService,
    IOnboardingWidgetState state,
    IProfileService profileService,
    IShiftSignupService signupService,
    IShiftManagementService shiftMgmt,
    IConsentService consents,
    IOnboardingService onboardingService,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    // [Authorize] guarantees NameIdentifier is present.
    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var step = await state.GetCurrentStepAsync(CurrentUserId(), ct);
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
        // Force explicit entry — OAuth-supplied names are unverified.
        return View(new NamesViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Names(NamesViewModel vm, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View(vm);

        var userId = CurrentUserId();

        // SaveProfileAsync does a full-field overwrite — bail if past Names step or we'd wipe data.
        var currentStep = await state.GetCurrentStepAsync(userId, ct);
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

        await profileService.SaveProfileAsync(userId, vm.BurnerName, request, language, ct);
        await onboardingService.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        return RedirectToAction(nameof(Shifts));
    }

    [HttpGet]
    public async Task<IActionResult> Shifts(string? priority = null, CancellationToken ct = default)
    {
        var es = await shiftMgmt.GetActiveAsync();
        if (es is null)
            return View(OnboardingShiftsBrowseModelBuilder.BuildEmpty(priority ?? string.Empty));

        // Stats line needs full event-wide set; priorityOnly:false then filter in builder.
        var urgentShifts = await shiftMgmt.GetBrowseShiftsAsync(
            es.Id, includeAdminOnly: false, includeSignups: true,
            includeHidden: false, priorityOnly: false);
        var (shiftIds, statuses) = await signupService.GetActiveSignupStatusesAsync(CurrentUserId(), es.Id);
        var vm = OnboardingShiftsBrowseModelBuilder.Build(
            es, urgentShifts, shiftIds, statuses, priority ?? string.Empty);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(Guid shiftId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        var result = await signupService.SignUpAsync(userId, shiftId, userId, false);
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
        // Multi-day Build/Strike signup. Mirrors ShiftsController but routes back through widget dispatcher.
        var result = await signupService.SignUpRangeAsync(CurrentUserId(), rotaId, startDayOffset, endDayOffset, isPrivileged: false);
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

        // Stub profile can't sign — ConsentService would refuse. Bounce to Names.
        if (await IsNameMissingAsync(userId, ct))
            return RedirectToNamesForStub();

        var rows = await consents.GetRequiredConsentRowsForUserAsync(userId, SystemTeamIds.Volunteers, ct);
        var unsigned = rows.Where(r => !r.Signed).ToList();
        if (unsigned.Count == 0)
            return RedirectToAction(nameof(Index));

        var next = unsigned[0];
        var detail = await consents.GetConsentReviewDetailAsync(next.DocumentVersionId, userId, ct);
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
            SetError(localizer["Consent_MustCheck"].Value);
            return RedirectToAction(nameof(Consents));
        }

        var userId = CurrentUserId();

        // Mirror GET gate against stale-page / back-button POST.
        if (await IsNameMissingAsync(userId, ct))
            return RedirectToNamesForStub();

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await consents.SubmitConsentAsync(
            userId, documentVersionId, explicitConsent: true, ipAddress, userAgent, ct);

        if (result.Success)
            await onboardingService.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        if (!result.Success)
        {
            switch (result.ErrorKey)
            {
                case "StubProfile":
                    return RedirectToNamesForStub();
                case "AlreadyConsented":
                    SetInfo(localizer["Consent_AlreadyConsented"].Value);
                    break;
            }
        }

        // Always dispatch — routes Home after final consent instead of stranding on signed-docs view.
        return RedirectToAction(nameof(Index));
    }

    // Gated on HasRequiredNameFields, not State==Stub — catches Active profiles with blank names.
    private async Task<bool> IsNameMissingAsync(Guid userId, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(userId, ct);
        return info is null || !info.HasRequiredNameFields;
    }

    private IActionResult RedirectToNamesForStub()
    {
        SetInfo(localizer["Consent_StubProfile_AddName"].Value);
        return RedirectToAction(nameof(Names));
    }
}
