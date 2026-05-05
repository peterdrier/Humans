using System.Security.Claims;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
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

    public OnboardingWidgetController(IOnboardingWidgetState state, IProfileService profileService)
    {
        _state = state;
        _profileService = profileService;
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
    public IActionResult Shifts() => throw new NotSupportedException("Shifts step is implemented in Task 5.");

    [HttpGet]
    public IActionResult Consents() => throw new NotSupportedException("Consents step is implemented in Task 7.");

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
