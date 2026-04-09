using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization requirement for team operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, resource, requirement)
/// where the resource is a Team.
/// </summary>
public sealed class TeamOperationRequirement : IAuthorizationRequirement
{
    public static readonly TeamOperationRequirement ManageCoordinators = new(nameof(ManageCoordinators));

    public string OperationName { get; }

    private TeamOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
