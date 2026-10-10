using NodaTime;
using Humans.Base.Interfaces;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;

namespace Humans.Issues.Services;

/// <summary>
/// The section's own service surface: what is left once <see cref="IIssuesRetention"/> (the
/// cleanup job) and <see cref="IIssueTriage"/> (the Backdoor machine API) have taken their
/// members into <c>Contracts/</c>.
/// Everything declared here has no consumer outside Issues — the screenshot-carrying submit
/// the in-app reporter uses, the viewer-scoped badge count, and the index page's reporter filter.
/// </summary>
internal interface IIssuesService : IApplicationService, IIssuesRetention, IIssueTriage
{
    /// <summary>
    /// Count of Open + Triage issues whose section maps to a role the viewer holds, plus
    /// their own. An Admin gets every Open + Triage issue. <c>InProgress</c> counts for
    /// nobody.
    /// </summary>
    Task<int> GetActionableCountForViewerAsync(IssueViewer viewer, CancellationToken ct = default);

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

    Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(CancellationToken ct = default);
}

/// <summary>A distinct reporter and how many issues they filed — the index page's filter list.</summary>
internal sealed record DistinctReporterRow(Guid UserId, string DisplayName, int Count);
