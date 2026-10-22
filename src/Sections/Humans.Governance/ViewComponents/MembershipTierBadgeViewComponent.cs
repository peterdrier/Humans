using Humans.Governance.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders nothing for a plain Volunteer, which is nearly everyone.</summary>
public sealed class MembershipTierBadgeViewComponent(IGovernanceService governanceService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var tier = await governanceService.GetMembershipTierAsync(userId, HttpContext.RequestAborted);

        return tier == MembershipTier.Volunteer ? Content(string.Empty) : View(tier);
    }
}
