using System.Security.Claims;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Handler for <see cref="CampComplianceAccessRequirement"/>. Short-circuits for
/// CampAdmin/Admin, otherwise admits any team/sub-team coordinator via the same
/// cached <see cref="IShiftManagementService.GetCoordinatorTeamIdsAsync"/> lookup
/// used by <see cref="IsAnyTeamManagerOrCoordinatorHandler"/>.
/// </summary>
public class CampComplianceAccessHandler(IShiftManagementService shiftManagement)
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
