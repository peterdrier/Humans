using Humans.Application.Interfaces.Users;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

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
///   "provision" — show email or provisioning form (TeamAdmin/Members)
///   "detail"    — show email + linked badge, or provisioning form (AdminDetail)
/// </summary>
public class NobodiesEmailBadgeViewComponent : ViewComponent
{
    private readonly IUserService _userService;

    public NobodiesEmailBadgeViewComponent(IUserService userService)
    {
        _userService = userService;
    }

    /// <summary>
    /// Renders a nobodies.team email badge for the given user.
    /// </summary>
    /// <param name="userId">The user to check.</param>
    /// <param name="mode">Display mode — see class doc.</param>
    /// <param name="teamSlug">Team slug for provisioning form (provision mode only).</param>
    /// <param name="displayName">User display name for confirm dialog (provision mode only).</param>
    public async Task<IViewComponentResult> InvokeAsync(
        Guid userId,
        string mode = "badge",
        string? teamSlug = null,
        string? displayName = null)
    {
        var info = await _userService.GetUserInfoAsync(userId);
        var nobodies = info?.UserEmails.FirstOrDefault(e => e.IsVerified
            && e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase));

        ViewBag.UserId = userId;
        ViewBag.HasEmail = nobodies is not null;
        ViewBag.Email = nobodies?.Email;
        ViewBag.IsPrimary = nobodies?.IsPrimary == true;
        ViewBag.Mode = mode;
        ViewBag.TeamSlug = teamSlug;
        ViewBag.DisplayName = displayName;

        return View();
    }
}
