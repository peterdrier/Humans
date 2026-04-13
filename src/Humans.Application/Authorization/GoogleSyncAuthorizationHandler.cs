using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

/// <summary>
/// Resource-based authorization handler for Google Workspace sync operations.
/// Enforces authorization at the service boundary so external Google API calls
/// are never made without a prior privilege check.
///
/// Authorization logic (matches docs/sections/Teams.md actor capabilities):
/// - System principal: allowed for all operations (background jobs).
/// - Admin: allowed for all operations (Preview, TeamResource, Execute).
/// - TeamsAdmin or Board: allowed for Preview (read sync state) and TeamResource
///   (link/unlink team groups and drive folders). Denied for Execute (sync actions).
/// - Everyone else: denied.
/// </summary>
public class GoogleSyncAuthorizationHandler : AuthorizationHandler<GoogleSyncOperationRequirement, string>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GoogleSyncOperationRequirement requirement,
        string operationName)
    {
        // Background jobs run as the system principal.
        if (SystemPrincipal.IsSystem(context.User))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Admin can do anything.
        if (context.User.IsInRole(RoleNames.Admin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // TeamsAdmin or Board may Preview and manage team-scoped resources, but cannot
        // Execute workspace-wide sync actions.
        var isTeamsOrBoard = context.User.IsInRole(RoleNames.TeamsAdmin) || context.User.IsInRole(RoleNames.Board);
        if (isTeamsOrBoard)
        {
            if (string.Equals(requirement.OperationName,
                    GoogleSyncOperationRequirement.Preview.OperationName,
                    StringComparison.Ordinal) ||
                string.Equals(requirement.OperationName,
                    GoogleSyncOperationRequirement.TeamResource.OperationName,
                    StringComparison.Ordinal))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
