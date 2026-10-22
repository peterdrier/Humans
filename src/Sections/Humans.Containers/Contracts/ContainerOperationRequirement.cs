using Microsoft.AspNetCore.Authorization;

namespace Humans.Containers.Contracts;

public enum ContainerOperation
{
    /// <summary>Create, edit or delete. Never phase-gated.</summary>
    Manage,

    /// <summary>Write a placement. A camp lead also needs the placement phase open.</summary>
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
