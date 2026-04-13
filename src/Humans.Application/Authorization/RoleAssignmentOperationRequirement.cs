using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

/// <summary>
/// Resource-based authorization requirement for role assignment operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, roleName, requirement)
/// where the resource is the target role name string.
/// </summary>
public sealed class RoleAssignmentOperationRequirement : IAuthorizationRequirement
{
    public static readonly RoleAssignmentOperationRequirement Manage = new(nameof(Manage));

    public string OperationName { get; }

    private RoleAssignmentOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
