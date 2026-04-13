using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Memory;

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
    private readonly IMemoryCache _cache;

    public UserAvatarViewComponent(
        IProfileService profileService,
        IUrlHelperFactory urlHelperFactory,
        IMemoryCache cache)
    {
        _profileService = profileService;
        _urlHelperFactory = urlHelperFactory;
        _cache = cache;
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
            // Warm the cache if cold — GetCachedProfile is a pure cache hit.
            var cached = _profileService.GetCachedProfile(userId)
                ?? await _profileService.GetCachedProfileAsync(userId);

            if (cached is not null)
            {
                displayName = cached.DisplayName;
                profilePictureUrl = ResolveAvatarUrl(cached);
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

    private string? ResolveAvatarUrl(CachedProfile cached)
    {
        // Cache key includes UpdatedAtTicks so a profile picture update busts the cache automatically.
        var cacheKey = $"useravatar:url:{cached.UserId}:{cached.UpdatedAtTicks}";
        return _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30);
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(4);

            if (cached.HasCustomPicture && cached.ProfileId != Guid.Empty)
            {
                var urlHelper = _urlHelperFactory.GetUrlHelper(ViewContext);
                return urlHelper.Action(
                    action: "Picture",
                    controller: "Profile",
                    values: new { id = cached.ProfileId, v = cached.UpdatedAtTicks });
            }

            return cached.ProfilePictureUrl;
        });
    }
}
