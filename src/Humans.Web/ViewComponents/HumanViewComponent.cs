using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Humans.Web.ViewComponents;

public enum HumanLayout { Text, Avatar, AvatarName, Card }

public enum HumanLink { None, Public, Admin }

public class HumanViewComponent : ViewComponent
{
    private readonly IProfileService _profileService;
    private readonly IUserService _userService;
    private readonly IUrlHelperFactory _urlHelperFactory;

    public HumanViewComponent(
        IProfileService profileService,
        IUserService userService,
        IUrlHelperFactory urlHelperFactory)
    {
        _profileService = profileService;
        _userService = userService;
        _urlHelperFactory = urlHelperFactory;
    }

    public async Task<IViewComponentResult> InvokeAsync(
        Guid userId,
        HumanLayout layout = HumanLayout.Text,
        HumanLink link = HumanLink.Public,
        bool popover = true,
        int size = 40,
        string? cssClass = null,
        string bgColor = "bg-secondary")
    {
        string? displayName = null;
        string? profilePictureUrl = null;

        if (userId != Guid.Empty)
        {
            var fullProfile = await _profileService.GetFullProfileAsync(userId);
            if (fullProfile is not null)
            {
                displayName = fullProfile.BurnerName;
                if (fullProfile.HasCustomPicture && fullProfile.ProfileId != Guid.Empty)
                {
                    var urlHelper = _urlHelperFactory.GetUrlHelper(ViewContext);
                    profilePictureUrl = urlHelper.Action(
                        action: "Picture",
                        controller: "Profile",
                        values: new { id = fullProfile.ProfileId, v = fullProfile.UpdatedAtTicks });
                }
            }
            else
            {
                var user = await _userService.GetByIdAsync(userId);
                displayName = user?.DisplayName;
            }
        }

        if (string.IsNullOrEmpty(displayName))
        {
            displayName = "Unknown";
        }

        string? href = null;
        if (link != HumanLink.None && userId != Guid.Empty)
        {
            var urlHelper = _urlHelperFactory.GetUrlHelper(ViewContext);
            href = link == HumanLink.Admin
                ? urlHelper.Action("AdminDetail", "Profile", new { id = userId })
                : urlHelper.Action("ViewProfile", "Profile", new { id = userId });
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
            ShowPopover = popover && link != HumanLink.None && userId != Guid.Empty,
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
    public string DisplayName { get; init; } = "";
    public string? ProfilePictureUrl { get; init; }
    public HumanLayout Layout { get; init; }
    public HumanLink Link { get; init; }
    public string? Href { get; init; }
    public bool ShowPopover { get; init; }
    public int Size { get; init; }
    public string? CssClass { get; init; }
    public string BgColor { get; init; } = "bg-secondary";
    public double FontRem { get; init; }
}
