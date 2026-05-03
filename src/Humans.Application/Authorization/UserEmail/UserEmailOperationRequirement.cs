using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization.UserEmail;

/// <summary>
/// Resource-based authorization requirement for user email operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, targetUserId, requirement)
/// where the resource is the target user's <see cref="Guid"/> id.
/// Self-or-admin gate: actor == target, or actor is in the Admin role.
/// </summary>
public sealed class UserEmailOperationRequirement : IAuthorizationRequirement
{
    public string Name { get; }

    public UserEmailOperationRequirement(string name)
    {
        Name = name;
    }
}
