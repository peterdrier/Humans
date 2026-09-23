using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Users.ViewComponents;

/// <summary>
/// Renders a nobodies.team email badge or status indicator for a user.
///
/// Reads from the cached <c>UserInfo</c> projection — no local IMemoryCache is needed
/// since the underlying read is already cache-served by <see cref="IUserService"/>.
///
/// Modes:
///   "badge"     — icon badge (ProfileCard)
///   "status"    — warning badge when email not primary (AdminList)
///   "email"     — show actual email address
///   "detail"    — show email + linked badge, or provisioning form (AdminDetail)
///
/// Public, not internal: Razor's build-time tag-helper discovery only sees public view
/// components (design; HUM0034 exempts view components), used in-assembly via
/// <c>&lt;vc:nobodies-email-badge&gt;</c>. Teams' member roster (formerly this component's
/// "provision" mode) now renders its own email-or-provisioning-form cell.
/// </summary>
public sealed class NobodiesEmailBadgeViewComponent(IUserServiceRead userService) : ViewComponent
{
    /// <summary>
    /// Renders a nobodies.team email badge for the given user.
    /// </summary>
    /// <param name="userId">The user to check.</param>
    /// <param name="mode">Display mode — see class doc.</param>
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, string mode = "badge")
    {
        var info = await userService.GetUserInfoAsync(userId);
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
