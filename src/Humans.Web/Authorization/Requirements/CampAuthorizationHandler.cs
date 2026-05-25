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
///   for the resource camp (resolved via <see cref="ICampService.IsUserCampEventManagerAsync"/>).
/// - Everyone else: deny.
/// </summary>
public class CampAuthorizationHandler(ICampService campService) : AuthorizationHandler<CampOperationRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CampOperationRequirement requirement)
    {
        var campId = context.Resource switch
        {
            CampInfo camp => camp.Id,
            Camp camp => camp.Id,
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

        var allowed = requirement.OperationName switch
        {
            nameof(CampOperationRequirement.Manage) =>
                await campService.IsUserCampLeadAsync(userId, campId.Value),
            nameof(CampOperationRequirement.SubmitEvent) =>
                await campService.IsUserCampEventManagerAsync(userId, campId.Value),
            _ => false
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }
    }
}
