using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

public sealed class ContainerPlacementRequirement : IAuthorizationRequirement
{
    public static readonly ContainerPlacementRequirement Place = new(nameof(Place));

    public string OperationName { get; }

    private ContainerPlacementRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
