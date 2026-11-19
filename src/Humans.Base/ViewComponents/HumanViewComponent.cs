using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;

namespace Humans.Base.ViewComponents;

public enum HumanLayout { Text, Avatar, AvatarName, Card }

/// <summary>
/// Where a person's name links. Every name links to the profile popup; the no-link values name the only
/// reasons a name may not (memory/architecture/burnername-is-the-display-name.md). A signed-out viewer never
/// gets a link, since profiles need a sign-in.
/// </summary>
public enum HumanLink
{
    /// <summary>The public profile, with the profile popup.</summary>
    Public,

    /// <summary>The admin record, with the profile popup.</summary>
    Admin,

    /// <summary>No link: the name sits inside another link, button or the popup itself.</summary>
    InsideControl,

    /// <summary>No link: a Gate kiosk screen, which is locked away from profile pages.</summary>
    Kiosk,
}

/// <summary>Renders a member’s name and picture, localizing missing names; its read honors browser cancellation.</summary>
public class HumanViewComponent(
    IUserService userService,
    IUrlHelperFactory urlHelperFactory,
    IStringLocalizer<SharedResource> localizer) : ViewComponent
{
    /// <summary>Renders resolved identity, or the exact account when <paramref name="raw"/> is true for admin merge displays.</summary>
    public async Task<IViewComponentResult> InvokeAsync(
        Guid userId,
        HumanLayout layout = HumanLayout.Text,
        HumanLink link = HumanLink.Public,
        bool popover = true,
        bool popoverPublic = false,
        int size = 40,
        string? cssClass = null,
        string bgColor = "bg-secondary",
        bool raw = false)
    {
        string? displayName = null;
        string? profilePictureUrl = null;

        if (userId != Guid.Empty)
        {
            var info = raw
                ? await userService.GetRawUserInfoAsync(userId, HttpContext.RequestAborted)
                : await userService.GetUserInfoAsync(userId, HttpContext.RequestAborted);
            if (info is not null)
            {
                displayName = info.BurnerName;
                profilePictureUrl = info.ProfilePictureUrl;
            }
        }

        if (string.IsNullOrEmpty(displayName))
        {
            displayName = localizer["Common_Unknown"];
        }

        var linked = link is HumanLink.Public or HumanLink.Admin
            && userId != Guid.Empty
            && HttpContext.User.Identity?.IsAuthenticated == true;
        string? href = null;
        if (linked)
        {
            var urlHelper = urlHelperFactory.GetUrlHelper(ViewContext);
            href = link == HumanLink.Admin
                ? urlHelper.Action("AdminDetail", "UsersAdmin", new { id = userId })
                : urlHelper.Action("ViewProfile", "ProfileView", new { id = userId });
        }

        var fontRem = Math.Round(size / 100.0 * 2.0, 1);
        if (fontRem < 0.7) fontRem = 0.7;

        var model = new HumanViewModel
        {
            UserId = userId,
            DisplayName = displayName,
            ProfilePictureUrl = profilePictureUrl,
            Layout = layout,
            Link = link,
            Href = href,
            ShowPopover = popover && linked,
            ShowPopoverPublic = popoverPublic && userId != Guid.Empty,
            Size = size,
            CssClass = cssClass,
            BgColor = bgColor,
            FontRem = fontRem,
        };

        return View(model);
    }
}

public class HumanViewModel
{
    public Guid UserId { get; init; }
    /// <summary>Member display name; fallback avatars show its first complete Unicode text element.</summary>
    public string DisplayName { get; init; } = "";
    public string? ProfilePictureUrl { get; init; }
    public HumanLayout Layout { get; init; }
    public HumanLink Link { get; init; }
    public string? Href { get; init; }
    public bool ShowPopover { get; init; }
    public bool ShowPopoverPublic { get; init; }
    public int Size { get; init; }
    public string? CssClass { get; init; }
    public string BgColor { get; init; } = "bg-secondary";
    public double FontRem { get; init; }
}
