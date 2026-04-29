using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization requirement for issue operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, issue, requirement)
/// where the resource is an Issue and the operation is one of:
/// <list type="bullet">
///   <item><description><see cref="Handle"/> — mutate status / assignee / section / GitHub link, or comment as a non-reporter.</description></item>
/// </list>
/// </summary>
public sealed class IssuesOperationRequirement : IAuthorizationRequirement
{
    public static readonly IssuesOperationRequirement Handle = new(nameof(Handle));

    public string OperationName { get; }

    private IssuesOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
