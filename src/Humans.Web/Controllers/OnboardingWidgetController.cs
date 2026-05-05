using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Services.Onboarding;
using Humans.Domain.Constants;
using Humans.Web.Models;
using Humans.Web.Models.OnboardingWidget;
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
    private readonly IProfileService _profileService;
    private readonly IShiftSignupService _signupService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IConsentService _consents;

    public OnboardingWidgetController(
        IOnboardingWidgetState state,
        IProfileService profileService,
        IShiftSignupService signupService,
        IShiftManagementService shiftMgmt,
        IConsentService consents)
    {
        _state = state;
        _profileService = profileService;
        _signupService = signupService;
        _shiftMgmt = shiftMgmt;
        _consents = consents;
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

        var userId = GetUserId();
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
            ProfilePictureData: null, ProfilePictureContentType: null, RemoveProfilePicture: false,
            SelectedTier: null, ApplicationMotivation: null, ApplicationAdditionalInfo: null,
            ApplicationSignificantContribution: null, ApplicationRoleUnderstanding: null);

        await _profileService.SaveProfileAsync(userId, vm.BurnerName, request, language, ct);

        return RedirectToAction(nameof(Shifts));
    }

    [HttpGet]
    public async Task<IActionResult> Shifts(bool showAll = false, CancellationToken ct = default)
    {
        var es = await _shiftMgmt.GetActiveAsync();
        ShiftBrowseViewModel browseModel;
        if (es is null)
        {
            browseModel = new ShiftBrowseViewModel { EventSettings = null!, ShowSignups = true, Sort = "urgency" };
        }
        else
        {
            // Onboarding only surfaces Event-period rotas. Build/Strike rotas
            // use a multi-day SignUpRange action that the widget doesn't
            // implement, and they're typically pre-organized through camp/
            // department channels — not the right thing to show a brand-new
            // volunteer on their first signup. Hidden + AdminOnly rotas are
            // already filtered at the data layer.
            var urgentShifts = (await _shiftMgmt.GetBrowseShiftsAsync(
                es.Id, includeAdminOnly: false, includeSignups: true,
                includeHidden: false, priorityOnly: !showAll))
                .Where(u => u.Shift.Rota.Period == Humans.Domain.Enums.RotaPeriod.Event)
                .ToList();
            var (shiftIds, statuses) = await _signupService.GetActiveSignupStatusesAsync(GetUserId(), es.Id);
            browseModel = OnboardingShiftsBrowseModelBuilder.Build(es, urgentShifts, shiftIds, statuses);
        }

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
        HttpContext.Session.SetString(OnboardingWidgetState.ShiftSkipSessionKey, "true");
        return RedirectToAction(nameof(Consents));
    }

    [HttpGet]
    public async Task<IActionResult> Consents(CancellationToken ct)
    {
        var userId = GetUserId();
        var rows = await _consents.GetRequiredConsentRowsForUserAsync(userId, SystemTeamIds.Volunteers, ct);
        var unsigned = rows.Where(r => !r.Signed).ToList();
        if (unsigned.Count == 0)
            return RedirectToAction(nameof(Index));

        var next = unsigned[0];
        var (version, _, _) = await _consents.GetConsentReviewDetailAsync(next.DocumentVersionId, userId, ct);
        if (version is null)
            return RedirectToAction(nameof(Index));

        var totalRequired = rows.Count;
        var currentIndex = totalRequired - unsigned.Count + 1;

        var vm = new ConsentsStepViewModel
        {
            DocumentVersionId = version.Id,
            DocumentName = version.LegalDocument.Name,
            VersionNumber = version.VersionNumber,
            Content = new Dictionary<string, string>(version.Content, StringComparer.Ordinal),
            ChangesSummary = version.ChangesSummary,
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
            TempData["Error"] = "MustCheck";
            return RedirectToAction(nameof(Consents));
        }

        var userId = GetUserId();
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();

        var result = await _consents.SubmitConsentAsync(
            userId, documentVersionId, explicitConsent: true, ipAddress, userAgent, ct);

        if (!result.Success)
            TempData["Error"] = result.ErrorKey;

        // Always go through the dispatcher so the user is routed Home once
        // the final required consent is signed, instead of stranded on the
        // signed-documents view. Failure path also dispatches — TempData
        // carries the error if the dispatcher routes back to Consents.
        return RedirectToAction(nameof(Index));
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
