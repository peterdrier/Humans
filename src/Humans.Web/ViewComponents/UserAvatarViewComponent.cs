using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Renders a user's avatar by looking up all display data from the cached profile
/// given only a user ID. Resolution precedence:
/// 1. Custom uploaded picture (served via <c>/Profile/{profileId}/Picture</c>)
/// 2. Google OAuth <c>User.ProfilePictureUrl</c>
/// 3. Initial-letter fallback from the display name
/// </summary>
public class UserAvatarViewComponent : ViewComponent
{
    private readonly IProfileService _profileService;
    private readonly IUrlHelperFactory _urlHelperFactory;

    public UserAvatarViewComponent(
        IProfileService profileService,
        IUrlHelperFactory urlHelperFactory)
    {
        _profileService = profileService;
        _urlHelperFactory = urlHelperFactory;
    }

    public async Task<IViewComponentResult> InvokeAsync(
        Guid userId,
        int size = 40,
        string? cssClass = null,
        string bgColor = "bg-secondary")
    {
        string? profilePictureUrl = null;
        string? displayName = null;

        if (userId != Guid.Empty)
        {
            var fullProfile = await _profileService.GetFullProfileAsync(userId);

            if (fullProfile is not null)
            {
                displayName = fullProfile.DisplayName;
                profilePictureUrl = ResolveAvatarUrl(fullProfile);
            }
            else if (IsCurrentUser(userId))
            {
                // Onboarding/guest users have no Profile row yet. Fall back to the Google
                // claims on the signed-in principal so their own avatar still renders in
                // the nav and dashboard until a Profile is created.
                displayName = UserClaimsPrincipal.FindFirstValue(ClaimTypes.Name);
                profilePictureUrl = UserClaimsPrincipal.FindFirstValue("urn:google:picture");
            }
        }

        var initial = string.IsNullOrEmpty(displayName) ? "?" : displayName[0].ToString();

        // Scale font size proportionally: roughly size * 0.4 as rem, capped reasonably
        var fontRem = Math.Round(size / 100.0 * 2.0, 1);
        if (fontRem < 0.7) fontRem = 0.7;

        ViewBag.ProfilePictureUrl = profilePictureUrl;
        ViewBag.Initial = initial;
        ViewBag.Size = size;
        ViewBag.CssClass = cssClass;
        ViewBag.BgColor = bgColor;
        ViewBag.FontRem = fontRem;

        return View();
    }

    private string? ResolveAvatarUrl(FullProfile fullProfile)
    {
        if (fullProfile.HasCustomPicture && fullProfile.ProfileId != Guid.Empty)
        {
            var urlHelper = _urlHelperFactory.GetUrlHelper(ViewContext);
            return urlHelper.Action(
                action: "Picture",
                controller: "Profile",
                values: new { id = fullProfile.ProfileId, v = fullProfile.UpdatedAtTicks });
        }

        return fullProfile.ProfilePictureUrl;
    }

    private bool IsCurrentUser(Guid userId)
    {
        var claim = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var currentUserId) && currentUserId == userId;
    }
}
