using Microsoft.AspNetCore.Authorization;

namespace Humans.Users.Authorization;

/// <summary>
/// Resource is the target user's <see cref="Guid"/> id.
/// Self-or-admin gate: actor == target, or actor is Admin.
/// </summary>
internal sealed class UserEmailOperationRequirement(string name) : IAuthorizationRequirement
{
    public string Name { get; } = name;
}
