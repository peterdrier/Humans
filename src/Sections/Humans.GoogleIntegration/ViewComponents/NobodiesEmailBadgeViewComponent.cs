using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.ViewComponents;

/// <summary>
/// Renders a nobodies.team email badge or status indicator for a user.
///
/// Reads the member's verified addresses from the cached <c>UserInfo</c> projection. Moved from
/// Users to GoogleIntegration (Users decomposition commit 15): the @nobodies.team account is the
/// Workspace account GoogleIntegration provisions.
///
/// Modes:
///   "badge"     — icon badge (ProfileCard)
///   "status"    — warning badge when email not primary (AdminList)
///   "email"     — show actual email address
///   "detail"    — show email + linked badge, or provisioning form (AdminDetail)
///
/// Public, not internal: Razor's build-time tag-helper discovery only sees public view
/// components (design; HUM0034 exempts view components), used by Users' views via
/// <c>&lt;vc:nobodies-email-badge&gt;</c> and this section's Google identity card. Teams' member
/// roster (formerly this component's "provision" mode) now renders its own
/// email-or-provisioning-form cell.
/// </summary>
public sealed class NobodiesEmailBadgeViewComponent(IUserService userService) : ViewComponent
{
    /// <summary>
    /// Renders a nobodies.team email badge for the given user.
    /// </summary>
    /// <param name="userId">The user to check.</param>
    /// <param name="mode">Display mode — see class doc.</param>
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, string mode = "badge")
    {
        var info = await userService.GetUserInfoAsync(userId, HttpContext.RequestAborted);
        var nobodies = info?.UserEmails.FirstOrDefault(e => e.IsVerified
            && e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase));

        ViewBag.UserId = info?.Id ?? userId;
        ViewBag.HasEmail = nobodies is not null;
        ViewBag.Email = nobodies?.Email;
        ViewBag.IsPrimary = nobodies?.IsPrimary == true;
        ViewBag.Mode = mode;

        return View();
    }
}
