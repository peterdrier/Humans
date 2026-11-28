using Microsoft.AspNetCore.Authorization;

namespace Humans.Auth.Contracts;

/// <summary>The resource is the target role name.</summary>
public sealed class SystemRoleOperationRequirement : IAuthorizationRequirement
{
    public static readonly SystemRoleOperationRequirement Manage = new();

    private SystemRoleOperationRequirement()
    {
    }
}
