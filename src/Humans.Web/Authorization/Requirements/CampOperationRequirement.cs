using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization requirement for camp operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, resource, requirement)
/// where the resource is a Camp.
/// </summary>
public sealed class CampOperationRequirement : IAuthorizationRequirement
{
    public static readonly CampOperationRequirement Manage = new(nameof(Manage));

    public string OperationName { get; }

    private CampOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
