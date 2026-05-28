using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
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
public class ContainerAuthorizationHandler(ICampServiceRead campService, ICityPlanningService cityPlanningService)
    : AuthorizationHandler<ContainerOperationRequirement, ContainerAuthorizationTarget>
{
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

        if (await cityPlanningService.IsCityPlanningTeamMemberAsync(userId))
        {
            context.Succeed(requirement);
            return;
        }

        var settings = await cityPlanningService.GetSettingsAsync();
        if (requirement.Operation == ContainerOperation.Place)
        {
            if (!settings.IsContainerPlacementOpen)
            {
                return;
            }
        }

        var camp = (await campService.GetCampsForYearAsync(settings.Year))
            .FirstOrDefault(c => c.Id == resource.CampId);
        if (camp?.Seasons.Any(s => s.Year == settings.Year && s.IsLead(userId)) == true)
        {
            context.Succeed(requirement);
        }
    }
}
