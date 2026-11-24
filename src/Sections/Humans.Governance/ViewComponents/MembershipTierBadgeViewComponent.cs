using Humans.Auth.Contracts;
using Humans.Governance.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Governance.ViewComponents;

/// <summary>Renders nothing for a plain Volunteer, which is nearly everyone. A Board seat outranks the tier.</summary>
public sealed class MembershipTierBadgeViewComponent(
    IGovernanceService governanceService,
    ISystemRoleService roleAssignments,
    IStringLocalizer<GovernanceResource> localizer) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId)
    {
        var ct = HttpContext.RequestAborted;
        if (await roleAssignments.IsUserBoardMemberAsync(userId, ct))
            return View((object)localizer["Governance_Status_Board"].Value);

        var tier = await governanceService.GetMembershipTierAsync(userId, ct);
        return tier == MembershipTier.Volunteer ? Content(string.Empty) : View((object)tier.ToString());
    }
}
