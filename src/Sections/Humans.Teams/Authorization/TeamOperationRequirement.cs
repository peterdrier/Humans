using Microsoft.AspNetCore.Authorization;

namespace Humans.Teams.Authorization;

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
