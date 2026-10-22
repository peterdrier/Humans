using Microsoft.AspNetCore.Authorization;

namespace Humans.Users.Authorization;

/// <summary>The resource is the target user's id.</summary>
internal sealed class UserEmailOperationRequirement(string name) : IAuthorizationRequirement
{
    public string Name { get; } = name;
}
