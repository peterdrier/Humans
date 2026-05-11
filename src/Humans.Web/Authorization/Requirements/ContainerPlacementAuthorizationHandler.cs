using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CitiPlanning;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for placing a container on the map.
///
/// Authorization logic:
/// - Admin / CampAdmin: allow any container
/// - City Planning team member: allow any container
/// - Org-level containers (CampId == SystemCampIds.Organization): only the above
/// - Camp lead: allow only barrio containers belonging to their camp,
///   and only while container placement phase is open
/// - Everyone else: deny
/// </summary>
public class ContainerPlacementAuthorizationHandler : AuthorizationHandler<ContainerPlacementRequirement, Container>
{
    private readonly ICampService _campService;
    private readonly ICityPlanningService _cityPlanningService;

    public ContainerPlacementAuthorizationHandler(
        ICampService campService,
        ICityPlanningService cityPlanningService)
    {
        _campService = campService;
        _cityPlanningService = cityPlanningService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ContainerPlacementRequirement requirement,
        Container resource)
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

        if (resource.CampId == SystemCampIds.Organization)
        {
            return;
        }

        var settings = await _cityPlanningService.GetSettingsAsync();
        if (!settings.IsContainerPlacementOpen)
        {
            return;
        }

        if (await _campService.IsUserCampLeadAsync(userId, resource.CampId))
        {
            context.Succeed(requirement);
        }
    }
}
