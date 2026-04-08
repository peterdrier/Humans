using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class UserAvatarViewComponent : ViewComponent
{
    private readonly IProfileService _profileService;

    public UserAvatarViewComponent(IProfileService profileService)
    {
        _profileService = profileService;
    }

    public IViewComponentResult Invoke(
        string? profilePictureUrl = null,
        string? displayName = null,
        int size = 40,
        string? cssClass = null,
        string bgColor = "bg-secondary",
        Guid? userId = null)
    {
        // Resolve from cache when userId is provided and displayName/profilePictureUrl are not
        if (userId.HasValue && userId.Value != Guid.Empty && string.IsNullOrEmpty(displayName))
        {
            var cached = _profileService.GetCachedProfile(userId.Value);
            if (cached is not null)
            {
                displayName = cached.DisplayName;
                profilePictureUrl ??= cached.ProfilePictureUrl;
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
}
