using System.Security.Claims;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.HumanLifecycle;
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
    IUserServiceRead userService,
    IOnboardingWidgetState state,
    IProfileEditorService profileEditorService,
    IShiftSignupService signupService,
    IShiftManagementService shiftMgmt,
    IShiftView shiftView,
    IConsentService consents,
    IOnboardingService onboardingService,
    IHumanLifecycleService humanLifecycle,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    private readonly IUserServiceRead _userService = userService;

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
            _ => RedirectToAction("Index", "Home"),
        };
    }

    [HttpGet]
    public async Task<IActionResult> Names(CancellationToken ct)
    {
        // Prefill from the user's OWN saved profile — never from OAuth claims, which
        // are unverified (see Names_Get_ReturnsBlankViewModel_IgnoringOauthClaims).
        // Bare/Stub accounts have blank names, so a genuinely new user still gets an
        // empty form; a returning or data-drifted user sees what's actually on file
        // instead of a blank form that looks like their earlier entry never saved.
        var profile = (await _userService.GetUserInfoAsync(CurrentUserId(), ct))?.Profile;
        return View(new NamesViewModel
        {
            BurnerName = profile?.BurnerName ?? string.Empty,
            FirstName = profile?.FirstName ?? string.Empty,
            LastName = profile?.LastName ?? string.Empty,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Names(NamesViewModel vm, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View(vm);

        var userId = CurrentUserId();
        var info = await _userService.GetUserInfoAsync(userId, ct);

        // Skip the save for an already-named user (stale page / back-button re-POST).
        // In-section check against the user's OWN profile — the name save must NOT be
        // gated by any cross-section consent/step check, or a vacuously-"complete"
        // consent state (no required legal docs) bounces a bare account into an endless
        // Names redirect loop.
        if (info is not null && info.HasRequiredNameFields)
            return RedirectToAction(nameof(Index));

        // SaveProfileAsync is a full-field overwrite but the form only carries the three
        // names, so preserve every other field from the current profile. A data-drifted
        // profile (City/Bio/emergency contact on file but a blank name) must keep that
        // data when its name is fixed here.
        var request = BuildNameSaveRequest(vm, info?.Profile);

        await profileEditorService.SaveProfileAsync(userId, vm.BurnerName, request, ct);
        await onboardingService.SetConsentCheckPendingIfEligibleAsync(userId, ct);

        return RedirectToAction(nameof(Shifts));
    }

    // Builds a profile-save request that sets the three name fields from the form while
    // carrying forward every other field from the existing profile (null for a brand-new
    // profileless account — nothing to preserve). Picture is left as a no-op mutation
    // (null data + RemoveProfilePicture:false) so SaveProfileAsync keeps the current one.
    private static ProfileSaveRequest BuildNameSaveRequest(NamesViewModel vm, ProfileInfo? existing) =>
        new(
            BurnerName: vm.BurnerName,
            FirstName: vm.FirstName,
            LastName: vm.LastName,
            City: existing?.City, CountryCode: existing?.CountryCode,
            Latitude: existing?.Latitude, Longitude: existing?.Longitude, PlaceId: existing?.PlaceId,
            Bio: existing?.Bio, Pronouns: existing?.Pronouns,
            ContributionInterests: existing?.ContributionInterests, BoardNotes: existing?.BoardNotes,
            BirthdayMonth: existing?.BirthdayMonth, BirthdayDay: existing?.BirthdayDay,
            EmergencyContactName: existing?.EmergencyContactName,
            EmergencyContactPhone: existing?.EmergencyContactPhone,
            EmergencyContactRelationship: existing?.EmergencyContactRelationship,
            NoPriorBurnExperience: existing?.NoPriorBurnExperience ?? false,
            ProfilePictureData: null, ProfilePictureContentType: null, RemoveProfilePicture: false,
            DietaryPreference: existing?.DietaryPreference,
            Allergies: existing?.Allergies.ToList(),
            AllergyOtherText: existing?.AllergyOtherText);

    [HttpGet]
    public async Task<IActionResult> Shifts(string? priority = null, CancellationToken ct = default)
    {
        var es = await shiftMgmt.GetActiveAsync();
        if (es is null)
            return View(OnboardingShiftsBrowseModelBuilder.BuildEmpty(priority ?? string.Empty));

        // Stats line needs full event-wide set; priorityOnly:false then filter in builder.
        var urgentShifts = await shiftMgmt.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            es.Id,
            Flags: ShiftBrowseQueryFlags.IncludeSignups));
        var userShiftView = await shiftView.GetUserAsync(CurrentUserId(), ct);
        var (shiftIds, statuses) = ShiftSignupHelper.ResolveActiveStatuses(userShiftView.Signups);
        var vm = OnboardingShiftsBrowseModelBuilder.Build(
            es, urgentShifts, shiftIds, statuses, priority ?? string.Empty);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(Guid shiftId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        var result = await signupService.SignUpAsync(userId, shiftId, actorUserId: userId);
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
        var result = await signupService.SignUpRangeAsync(CurrentUserId(), rotaId, startDayOffset, endDayOffset);
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
        {
            // Self-heal a consent-suspended user who is already compliant (nothing left to sign —
            // e.g. the required set shrank after they were suspended). Without this they loop
            // Status → Consents → Index → Home → Status forever, since the un-suspend otherwise only
            // fires on a fresh signature. No-op for any non-Suspended user.
            await humanLifecycle.RestoreConsentSuspensionAsync(userId, ct);
            return RedirectToAction(nameof(Index));
        }

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
