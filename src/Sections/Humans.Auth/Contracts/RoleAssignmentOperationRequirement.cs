using Microsoft.AspNetCore.Authorization;

namespace Humans.Auth.Contracts;

/// <summary>The resource is the target role name.</summary>
public sealed class RoleAssignmentOperationRequirement : IAuthorizationRequirement
{
    public static readonly RoleAssignmentOperationRequirement Manage = new(nameof(Manage));

    public string OperationName { get; }

    private RoleAssignmentOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
