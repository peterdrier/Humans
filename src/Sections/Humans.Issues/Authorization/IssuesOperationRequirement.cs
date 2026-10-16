using Microsoft.AspNetCore.Authorization;

namespace Humans.Issues.Authorization;

/// <summary>
/// The requirement for handling an issue: mutate status / assignee / section / GitHub link, or comment as a non-reporter.
/// </summary>
internal sealed class IssuesOperationRequirement : IAuthorizationRequirement
{
    public static readonly IssuesOperationRequirement Handle = new();

    private IssuesOperationRequirement()
    {
    }
}
