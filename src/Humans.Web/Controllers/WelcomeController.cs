using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[AllowAnonymous]
public class WelcomeController : Controller
{
    [HttpGet("/Welcome")]
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated ?? false)
        {
            var isActive = User.HasClaim(
                Authorization.RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
                Authorization.RoleAssignmentClaimsTransformation.ActiveClaimValue);

            if (isActive)
            {
                return Redirect("/Shifts");
            }

            // Not active — send to widget, not explainer.
            return Redirect("/OnboardingWidget");
        }

        return View();
    }
}
