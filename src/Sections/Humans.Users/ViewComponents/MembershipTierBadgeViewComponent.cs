using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>
/// The member's tier badge beside their name on the dashboard. Renders nothing for a plain
/// Volunteer, which is nearly everyone.
/// </summary>
public sealed class MembershipTierBadgeViewComponent(IUserServiceRead userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var tier = (await userService.GetUserInfoAsync(userId))?.Profile?.MembershipTier ?? MembershipTier.Volunteer;

        return tier == MembershipTier.Volunteer ? Content(string.Empty) : View(tier);
    }
}
