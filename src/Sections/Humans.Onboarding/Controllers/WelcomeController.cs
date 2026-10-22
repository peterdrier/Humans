using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Onboarding.Controllers;

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

            return Redirect("/OnboardingWidget");
        }

        return View();
    }
}
