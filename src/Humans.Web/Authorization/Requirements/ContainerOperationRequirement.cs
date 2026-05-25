using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

public enum ContainerOperation
{
    /// <summary>
    /// Create / edit / delete a container. Allowed for admins, city-planning
    /// team members, and the owning camp's leads.
    /// </summary>
    Manage,

    /// <summary>
    /// Place a container on the map (write a <c>ContainerPlacement</c> row).
    /// Same actors as <see cref="Manage"/>, with an extra gate on the camp-lead
    /// branch: placement phase must be open.
    /// </summary>
    Place,
}

public sealed class ContainerOperationRequirement : IAuthorizationRequirement
{
    public static readonly ContainerOperationRequirement Manage = new(ContainerOperation.Manage);
    public static readonly ContainerOperationRequirement Place = new(ContainerOperation.Place);

    public ContainerOperation Operation { get; }

    private ContainerOperationRequirement(ContainerOperation operation)
    {
        Operation = operation;
    }
}
