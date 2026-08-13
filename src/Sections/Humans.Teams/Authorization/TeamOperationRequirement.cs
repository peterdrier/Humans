using Microsoft.AspNetCore.Authorization;

namespace Humans.Teams.Authorization;

/// <summary>
/// Resource-based authorization requirement for team operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, resource, requirement)
/// where the resource is a Team.
/// </summary>
internal sealed class TeamOperationRequirement : IAuthorizationRequirement
{
    public static readonly TeamOperationRequirement ManageCoordinators = new(nameof(ManageCoordinators));
    public static readonly TeamOperationRequirement ManageEarlyEntry = new(nameof(ManageEarlyEntry));

    public string OperationName { get; }

    private TeamOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
