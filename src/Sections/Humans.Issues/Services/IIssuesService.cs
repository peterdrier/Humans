using NodaTime;
using Humans.Base.Interfaces;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;

namespace Humans.Issues.Services;

/// <summary>
/// The section's own service surface: what is left once <see cref="IIssuesRetention"/> (the
/// cleanup job) and <see cref="IIssueTriage"/> (the Backdoor machine API,
/// nobodies-collective/Humans#1128) have taken their members onto the contracts leaf.
/// Everything declared here has no consumer outside Issues — the screenshot-carrying submit
/// the in-app reporter uses, the result-returning mutation overloads its own controller
/// prefers, the viewer-scoped badge count, and the index page's reporter filter.
/// </summary>
internal interface IIssuesService : IApplicationService, IIssuesRetention, IIssueTriage
{
    /// <summary>
    /// Count of Open + Triage issues whose section maps to a role the viewer holds, plus
    /// their own non-terminal issues. Admins get the global non-terminal count.
    /// </summary>
    Task<int> GetActionableCountForViewerAsync(
        Guid viewerUserId, IReadOnlyList<string> viewerRoles, bool viewerIsAdmin,
        CancellationToken ct = default);

    Task<Issue> SubmitIssueAsync(
        Guid reporterUserId,
        IssueCategory category,
        string title,
        string description,
        string? section,
        string? pageUrl,
        string? userAgent,
        string? additionalContext,
        IFormFile? screenshot,
        LocalDate? dueDate = null,
        IReadOnlyList<string>? reporterRoles = null,
        CancellationToken ct = default);

    Task<IssueMutationResult> UpdateStatusWithResultAsync(
        Guid issueId, IssueStatus newStatus, Guid? actorUserId, CancellationToken ct = default);

    Task<IssueMutationResult> UpdateAssigneeWithResultAsync(
        Guid issueId, Guid? newAssigneeUserId, Guid? actorUserId, CancellationToken ct = default);

    Task<IssueMutationResult> UpdateSectionWithResultAsync(
        Guid issueId, string? newSection, Guid? actorUserId, CancellationToken ct = default);

    Task<IssueMutationResult> SetGitHubIssueNumberWithResultAsync(
        Guid issueId, int? githubIssueNumber, Guid? actorUserId, CancellationToken ct = default);

    Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(CancellationToken ct = default);
}

/// <summary>A distinct reporter and how many issues they filed — the index page's filter list.</summary>
internal sealed record DistinctReporterRow(Guid UserId, string DisplayName, int Count);

internal sealed record IssueMutationResult(bool Succeeded, bool NotFound, string? ErrorMessage)
{
    public static IssueMutationResult Success() => new(true, false, null);

    public static IssueMutationResult Missing(string message) => new(false, true, message);

    public static IssueMutationResult Failed(string message) => new(false, false, message);
}
