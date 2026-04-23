using System.Security.Claims;
using Humans.Application.Interfaces.Camps;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for camp operations.
/// Evaluates whether a user can perform management operations on a specific Camp.
///
/// Authorization logic:
/// - Admin: allow any camp
/// - CampAdmin: allow any camp
/// - Camp lead: allow only their assigned camp
/// - Everyone else: deny
/// </summary>
public class CampAuthorizationHandler : AuthorizationHandler<CampOperationRequirement, Camp>
{
    private readonly ICampService _campService;

    public CampAuthorizationHandler(ICampService campService)
    {
        _campService = campService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CampOperationRequirement requirement,
        Camp resource)
    {
        // Admin and CampAdmin can manage any camp
        if (RoleChecks.IsCampAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        // Check if user is a lead for this specific camp
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        if (await _campService.IsUserCampLeadAsync(userId, resource.Id))
        {
            context.Succeed(requirement);
        }
    }
}
