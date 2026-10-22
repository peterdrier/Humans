using Humans.Base.Controllers;
using System.Security.Claims;
using Humans.Consent;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Onboarding.Models;
using Humans.Onboarding.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Onboarding.Controllers;

/// <summary>Index is the dispatcher: everything links there without knowing which step a user is on.</summary>
[Authorize]
internal sealed class OnboardingWidgetController(
    IUserService userService,
    IOnboardingWidgetState state,
    IProfileEditorService profileEditorService,
    IShiftSignups signupService,
    IShiftService shiftMgmt,
    ISettingsService settingsService,
    IShiftView shiftView,
    IConsentSubmission consents,
    IOnboardingService onboardingService,
    IClock clock,
    IStringLocalizer<OnboardingResource> localizer,
    IStringLocalizer<ConsentResource> consentLocalizer,
    ILogger<OnboardingWidgetController> logger) : HumansControllerBase(userService)
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
            _ => RedirectToAction("Index", "Home"),
        };
    }

    [HttpGet]
    public async Task<IActionResult> Names(CancellationToken ct)
    {
        // Prefill from the user's own saved profile, never from OAuth claims, which are unverified.
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
        userId = info?.Id ?? userId;

        // Skip the save for an already-named user (stale page or back-button re-POST). Must not be gated
        // on any consent/step check: with no required documents that reads "complete" and loops forever.
        if (info is not null && info.HasRequiredNameFields)
            return RedirectToAction(nameof(Index));

        // SaveProfileAsync overwrites every field but the form carries only names, so keep the rest
        // from the current profile.
        var request = BuildNameSaveRequest(vm, info?.Profile);

        var save = await profileEditorService.SaveProfileAsync(userId, request, ct);
        if (!save.Succeeded)
        {
            ModelState.AddModelError(string.Empty, localizer["Onboarding_ProfileSaveFailed"]);
            return View(vm);
        }
        await onboardingService.ProfileSavedAsync(userId, ct);

        return RedirectToAction(nameof(Shifts));
    }

    // Picture is a no-op mutation (null data, RemoveProfilePicture: false) so the current one stays.
    // Dietary fields and CV are omitted: Users leaves them untouched on a name-only save.
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
            ProfilePictureData: null, ProfilePictureContentType: null, RemoveProfilePicture: false);

    [HttpGet]
    public async Task<IActionResult> Shifts(string? priority = null, CancellationToken ct = default)
    {
        var es = await settingsService.GetActiveEventSettingsAsync(ct);
        if (es is null)
            return View(OnboardingShiftsStepBuilder.BuildEmpty(priority ?? string.Empty));

        // Stats line needs full event-wide set; priorityOnly:false then filter in builder.
        var urgentShifts = await shiftMgmt.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            es.Id,
            Flags: ShiftBrowseQueryFlags.IncludeSignups), ct);
        var userId = (await GetCurrentUserInfoAsync(ct))?.Id ?? CurrentUserId();
        var userShiftView = await shiftView.GetUserAsync(userId, ct);
        var (shiftIds, statuses) = ShiftSignupHelper.ResolveActiveStatuses(userShiftView.Signups);
        // Onboarding viewers are never privileged, so the early-entry lock applies once the close has passed.
        var earlyEntryClosed = es.IsEarlyEntryClosed(clock.GetCurrentInstant());
        var vm = OnboardingShiftsStepBuilder.Build(
            es, urgentShifts, shiftIds, statuses, priority ?? string.Empty, earlyEntryClosed);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(Guid shiftId)
    {
        var userId = CurrentUserId();
        var result = await signupService.SignUpAsync(userId, shiftId, actorUserId: userId);
        if (!result.Success)
        {
            SetError(result.Error ?? localizer["Onboarding_ShiftsSignUpFailed"].Value);
            return RedirectToAction(nameof(Shifts));
        }
        if (result.Warning is { } warning)
            SetInfo(warning);
        return RedirectToAction(nameof(Consents));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUpRange(Guid rotaId, [BindRequired] int startDayOffset, [BindRequired] int endDayOffset)
    {
        var userId = CurrentUserId();
        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed range signup for {UserId} on {RotaId}", userId, rotaId);
            return BadRequest(ModelState);
        }

        var result = await signupService.SignUpRangeAsync(userId, rotaId, startDayOffset, endDayOffset);
        if (!result.Success)
        {
            SetError(result.Error ?? localizer["Onboarding_ShiftsSignUpRangeFailed"].Value);
            return RedirectToAction(nameof(Shifts));
        }
        if (result.Warning is { } warning)
            SetInfo(warning);
        return RedirectToAction(nameof(Consents));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Skip()
    {
        HttpContext.Session.SetString(HttpOnboardingWidgetSessionState.ShiftSkipSessionKey, "true");
        return RedirectToAction(nameof(Consents));
    }

    [HttpGet]
    public async Task<IActionResult> Consents(CancellationToken ct)
    {
        var userId = CurrentUserId();

        // ConsentService refuses a nameless signer.
        if (await IsNameMissingAsync(userId, ct))
            return RedirectToNamesForStub();

        var step = await onboardingService.GetNextUnsignedConsentAsync(userId, ct);
        if (step.Outcome is NextConsentStepOutcome.NothingLeftToSign)
            return RedirectToAction(nameof(Index));

        // The dispatcher still answers "consents" while anything is unsigned, so handing an unloadable
        // document back to it would bounce the user between the two forever.
        if (step.Outcome is NextConsentStepOutcome.DocumentUnavailable)
        {
            SetError(localizer["Onboarding_ConsentDocumentUnavailable"].Value);
            return RedirectToAction("Index", "Home");
        }

        // Non-null by the outcome contract on NextConsentStepData.
        var next = step.Next!;
        var vm = new ConsentsStepViewModel
        {
            DocumentVersionId = next.DocumentVersionId,
            DocumentName = next.DocumentName,
            VersionNumber = next.VersionNumber,
            Content = new Dictionary<string, string>(next.Content, StringComparer.Ordinal),
            ChangesSummary = next.ChangesSummary,
            CurrentIndex = step.CurrentIndex,
            TotalRequired = step.TotalRequired,
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignConsent(Guid documentVersionId, bool explicitConsent, CancellationToken ct)
    {
        if (!explicitConsent)
        {
            SetError(consentLocalizer["Consent_MustCheck"].Value);
            return RedirectToAction(nameof(Consents));
        }

        var userId = CurrentUserId();

        // Same gate as GET, against a stale-page or back-button POST.
        if (await IsNameMissingAsync(userId, ct))
            return RedirectToNamesForStub();

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await consents.SubmitConsentAsync(
            userId, documentVersionId, explicitConsent: true, ipAddress, userAgent, ct);

        if (result.Success)
            await onboardingService.ConsentSignedAsync(userId, ct);

        if (!result.Success)
        {
            switch (result.ErrorKey)
            {
                case "StubProfile":
                    return RedirectToNamesForStub();
                case "AlreadyConsented":
                    SetInfo(consentLocalizer["Consent_AlreadyConsented"].Value);
                    break;
            }
        }

        // Always dispatch: after the final consent it routes Home instead of stranding the user here.
        return RedirectToAction(nameof(Index));
    }

    // HasRequiredNameFields, not State == Stub: catches Active profiles with blank names.
    private async Task<bool> IsNameMissingAsync(Guid userId, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(userId, ct);
        return info is null || !info.HasRequiredNameFields;
    }

    private IActionResult RedirectToNamesForStub()
    {
        SetInfo(consentLocalizer["Consent_StubProfile_AddName"].Value);
        return RedirectToAction(nameof(Names));
    }
}
