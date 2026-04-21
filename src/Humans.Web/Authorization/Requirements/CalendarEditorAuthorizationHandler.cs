using System.Security.Claims;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based handler for editing a CalendarEvent.
/// Resource = the owning <see cref="Team"/>.
/// Succeeds if the user is Admin OR a coordinator of the owning team.
/// </summary>
public class CalendarEditorAuthorizationHandler
    : AuthorizationHandler<CalendarEditorRequirement, Team>
{
    private readonly ITeamService _teamService;

    public CalendarEditorAuthorizationHandler(ITeamService teamService)
    {
        _teamService = teamService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CalendarEditorRequirement requirement,
        Team resource)
    {
        if (RoleChecks.IsAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        if (await _teamService.IsUserCoordinatorOfTeamAsync(resource.Id, userId))
        {
            context.Succeed(requirement);
        }
    }
}
