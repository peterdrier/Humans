using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Shifts.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Camps.Authorization;

/// <summary>
/// Handler for <see cref="CampComplianceAccessRequirement"/>. Short-circuits for
/// CampAdmin/Admin, otherwise admits any team/sub-team coordinator via the cached
/// <see cref="IShiftManagementServiceRead.GetCoordinatorTeamIdsAsync"/> lookup.
/// Moved from Humans.Shifts at nobodies-collective/Humans#1091: the policy, its
/// consumers and its registration are all this section's; the coordinator lookup
/// is a cross-section read through Shifts' contracts leaf.
/// </summary>
internal sealed class CampComplianceAccessHandler(IShiftManagementServiceRead shiftManagement)
    : AuthorizationHandler<CampComplianceAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CampComplianceAccessRequirement requirement)
    {
        var user = context.User;

        if (user.IsInRole(RoleNames.Admin) || user.IsInRole(RoleNames.CampAdmin))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var coordinatedTeamIds = await shiftManagement.GetCoordinatorTeamIdsAsync(userId);
        if (coordinatedTeamIds.Count > 0)
            context.Succeed(requirement);
    }
}
