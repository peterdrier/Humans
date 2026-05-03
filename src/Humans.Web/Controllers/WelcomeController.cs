using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[AllowAnonymous]
public class WelcomeController : Controller
{
    [HttpGet("/Welcome")]
    public IActionResult Index()
    {
        // If user is already authenticated and an active member,
        // skip the welcome page and go straight to shifts.
        if (User.Identity?.IsAuthenticated ?? false)
        {
            var isActive = User.HasClaim(
                Authorization.RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
                Authorization.RoleAssignmentClaimsTransformation.ActiveClaimValue);

            if (isActive)
            {
                return Redirect("/Shifts");
            }
        }

        return View();
    }
}
