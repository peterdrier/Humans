using Humans.Users.Contracts;
using Humans.Users.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>
/// The widget gallery's two <see cref="ProfileSummaryViewModel"/> cards — <c>_ProfileCard</c>
/// and <c>_HumanPopover</c> — rendered against a synthetic sample built from the current
/// human. The sample type is internal to this section, so the section renders the cards
/// itself, ShiftsGallery's shape (nobodies-collective/Humans#1091). Invoked by name from
/// <c>WidgetGallery/Index.cshtml</c> and discovered by Shell's
/// <c>SectionViewComponentFeatureProvider</c>, so it stays <c>internal</c>.
/// </summary>
internal sealed class UsersGalleryViewComponent(IUserServiceRead userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid currentUserId, string displayName)
    {
        var info = await userService.GetUserInfoAsync(currentUserId);
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
        return View(sample);
    }
}
