using Humans.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

/// <summary>
/// Resource-based authorization handler for Google Workspace sync operations.
/// Enforces authorization at the service boundary so external Google API calls
/// are never made without a prior privilege check.
///
/// Authorization logic:
/// - System principal: allowed for all operations (background jobs).
/// - Admin: allowed for all operations (Execute and Preview).
/// - TeamsAdmin or Board: allowed for Preview-only operations (read sync state).
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

        // Admin can do anything — Execute and Preview.
        if (context.User.IsInRole(RoleNames.Admin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // TeamsAdmin or Board may Preview (read-only) but cannot Execute mutations.
        if (string.Equals(requirement.OperationName,
                GoogleSyncOperationRequirement.Preview.OperationName,
                StringComparison.Ordinal))
        {
            if (context.User.IsInRole(RoleNames.TeamsAdmin) || context.User.IsInRole(RoleNames.Board))
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}
