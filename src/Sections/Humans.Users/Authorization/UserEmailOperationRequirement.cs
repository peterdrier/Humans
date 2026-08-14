using Microsoft.AspNetCore.Authorization;
using Humans.Users.Contracts;

namespace Humans.Users.Authorization;

/// <summary>
/// Resource-based authorization requirement for user email operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, targetUserId, requirement)
/// where the resource is the target user's <see cref="Guid"/> id.
/// Self-or-admin gate: actor == target, or actor is in the Admin role.
/// </summary>
internal sealed class UserEmailOperationRequirement(string name) : IAuthorizationRequirement
{
    public string Name { get; } = name;
}
