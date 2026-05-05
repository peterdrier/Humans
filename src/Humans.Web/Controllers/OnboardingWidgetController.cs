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
    public IActionResult Names() => throw new NotSupportedException("Names step is implemented in Task 3.");

    [HttpGet]
    public IActionResult Shifts() => throw new NotSupportedException("Shifts step is implemented in Task 5.");

    [HttpGet]
    public IActionResult Consents() => throw new NotSupportedException("Consents step is implemented in Task 7.");

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
