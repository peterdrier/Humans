using Humans.Base.Authorization;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Permissions.Contracts;

/// <summary>The resource is the <see cref="Owner"/> the action is on.</summary>
public sealed class PermissionRequirement(Permission permission) : IAuthorizationRequirement, IDescribedRequirement
{
    public string Key { get; } = permission.Key;

    public IReadOnlyList<string> Admits { get; } =
        [RoleNames.Admin, .. permission.AdminRoles, $"{permission.Key} holders"];
}
