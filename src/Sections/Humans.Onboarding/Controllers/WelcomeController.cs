using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Onboarding.Controllers;

/// <summary>
/// Anonymous explainer + redirect gate at /Welcome: signed-in actives go to /Shifts,
/// signed-in non-actives to the onboarding widget, everyone else sees the explainer.
/// Moved from Shell with the rest of the onboarding entry points
/// (nobodies-collective/Humans#1091).
/// </summary>
[AllowAnonymous]
internal sealed class WelcomeController : Controller
{
    [HttpGet("/Welcome")]
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated ?? false)
        {
            if (RoleChecks.IsActiveMember(User))
            {
                return Redirect("/Shifts");
            }

            // Not active — send to widget, not explainer.
            return Redirect("/OnboardingWidget");
        }

        return View();
    }
}
