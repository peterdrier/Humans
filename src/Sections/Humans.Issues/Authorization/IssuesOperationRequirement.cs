using Microsoft.AspNetCore.Authorization;

namespace Humans.Issues.Authorization;

/// <summary>Mutate status, assignee, section or GitHub link, or comment as a non-reporter.</summary>
internal sealed class IssuesOperationRequirement : IAuthorizationRequirement
{
    public static readonly IssuesOperationRequirement Handle = new();

    private IssuesOperationRequirement()
    {
    }
}
