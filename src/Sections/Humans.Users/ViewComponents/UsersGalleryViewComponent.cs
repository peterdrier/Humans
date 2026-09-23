using System.Security.Claims;
using Humans.Users.Contracts;
using Humans.Users.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>
/// The widget gallery's Users-section samples, contributed to
/// <see cref="Humans.Base.Interfaces.ChromeSlots.WidgetGallery"/> via <see cref="SectionChrome"/>:
/// the two <see cref="ProfileSummaryViewModel"/> cards (<c>_ProfileCard</c>, <c>_HumanPopover</c>,
/// whose sample type is internal to this section) plus &lt;vc:profile-card&gt;,
/// &lt;vc:human-summary&gt;, &lt;vc:communication-preferences-panel&gt;,
/// &lt;vc:nobodies-email-badge&gt; and &lt;vc:membership-tier-badge&gt; rendered against the
/// signed-in admin.
/// </summary>
/// <remarks>
/// A chrome-slot contribution takes no arguments (<c>ISectionChrome</c>'s
/// <c>[ViewComponentSlot]</c> declares none), so this reads the current user from claims rather
/// than a caller-supplied id — the page's own <c>CurrentUserId</c>/<c>CurrentUserDisplayName</c>
/// are for the widgets it still renders itself.
/// </remarks>
internal sealed class UsersGalleryViewComponent(IUserServiceRead userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var currentUserId))
            return Content(string.Empty);

        var info = await userService.GetUserInfoAsync(currentUserId);
        var displayName = string.IsNullOrEmpty(info?.BurnerName) ? "Current user" : info.BurnerName;
        var sample = new ProfileSummaryViewModel
        {
            UserId = currentUserId,
            DisplayName = displayName,
            Email = info?.Email,
            MembershipStatus = "Active",
            MembershipTier = "Volunteer",
            IsSuspended = false,
            PreferredLanguage = info?.PreferredLanguage,
            Teams = ["Fire Conclave"],
        };
        return View(new UsersGalleryViewModel(currentUserId, sample));
    }
}

internal sealed record UsersGalleryViewModel(Guid CurrentUserId, ProfileSummaryViewModel Sample);
