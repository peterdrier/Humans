using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for camp operations.
///
/// Authorization logic:
/// - Admin / CampAdmin: allow any camp (both operations).
/// - <see cref="CampOperationRequirement.Manage"/>: Camp lead for the resource camp.
/// - <see cref="CampOperationRequirement.SubmitEvent"/>: Lead OR Workshop role holder
///   for the resource camp.
/// - Everyone else: deny.
/// </summary>
public class CampAuthorizationHandler(ICampServiceRead campService) : AuthorizationHandler<CampOperationRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CampOperationRequirement requirement)
    {
        var resourceCamp = context.Resource as CampInfo;
        var campId = context.Resource switch
        {
            CampInfo campInfo => campInfo.Id,
            Camp campEntity => campEntity.Id,
            Guid id => id,
            _ => (Guid?)null
        };

        if (campId is null)
            return;

        if (RoleChecks.IsCampAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        if (requirement.OperationName is not nameof(CampOperationRequirement.Manage)
            and not nameof(CampOperationRequirement.SubmitEvent))
        {
            return;
        }

        var camp = resourceCamp ?? await GetPublicYearCampAsync(campId.Value);
        var allowed = requirement.OperationName switch
        {
            nameof(CampOperationRequirement.Manage) =>
                camp?.IsLead(userId) == true,
            nameof(CampOperationRequirement.SubmitEvent) =>
                camp?.IsEventManager(userId) == true,
            _ => false
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }
    }

    private async Task<CampInfo?> GetPublicYearCampAsync(Guid campId)
    {
        var settings = await campService.GetSettingsAsync();
        return (await campService.GetCampsForYearAsync(settings.PublicYear))
            .FirstOrDefault(c => c.Id == campId);
    }
}
