using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CitiPlanning;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for container operations.
///
/// Authorization logic:
/// - Admin / CampAdmin: allow any container
/// - City Planning team member: allow any container
/// - Camp lead: allow only containers belonging to their camp; for
///   <see cref="ContainerOperation.Place"/> the placement phase must also be open
/// - Everyone else: deny
/// </summary>
public class ContainerAuthorizationHandler : AuthorizationHandler<ContainerOperationRequirement, ContainerAuthorizationTarget>
{
    private readonly ICampService _campService;
    private readonly ICityPlanningService _cityPlanningService;

    public ContainerAuthorizationHandler(ICampService campService, ICityPlanningService cityPlanningService)
    {
        _campService = campService;
        _cityPlanningService = cityPlanningService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ContainerOperationRequirement requirement,
        ContainerAuthorizationTarget resource)
    {
        if (RoleChecks.IsCampAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            return;
        }

        if (await _cityPlanningService.IsCityPlanningTeamMemberAsync(userId))
        {
            context.Succeed(requirement);
            return;
        }

        if (requirement.Operation == ContainerOperation.Place)
        {
            var settings = await _cityPlanningService.GetSettingsAsync();
            if (!settings.IsContainerPlacementOpen)
            {
                return;
            }
        }

        if (await _campService.IsUserCampLeadAsync(userId, resource.CampId))
        {
            context.Succeed(requirement);
        }
    }
}
