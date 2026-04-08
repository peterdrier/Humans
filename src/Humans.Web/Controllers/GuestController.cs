using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Domain.Entities;

namespace Humans.Web.Controllers;

/// <summary>
/// Dashboard for profileless accounts (authenticated users without a Profile).
/// Phase A shell: basic landing page with placeholder sections.
/// Later phases will add comms preferences, GDPR tools, ticket status, and create-profile CTA.
/// </summary>
[Authorize]
public class GuestController : HumansControllerBase
{
    public GuestController(UserManager<User> userManager)
        : base(userManager)
    {
    }

    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        ViewData["DisplayName"] = user.DisplayName;
        return View();
    }
}
