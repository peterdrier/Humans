using NodaTime;
using Humans.Base.Interfaces;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;

namespace Humans.Issues.Services;

internal interface IIssuesService : IApplicationService, IIssuesRetention, IIssueTriage
{
    /// <summary>
    /// Open + Triage issues in the viewer's sections, plus their own; all of them for Admin. <c>InProgress</c>
    /// counts for nobody.
    /// </summary>
    Task<int> GetActionableCountForViewerAsync(IssueViewer viewer, CancellationToken ct = default);

    Task<IssueSubmissionResult> SubmitIssueAsync(
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

internal sealed record DistinctReporterRow(Guid UserId, string DisplayName, int Count);

/// <summary>A saved issue or an upload refusal the member-facing controller localizes.</summary>
internal sealed record IssueSubmissionResult(Issue? Issue, string? ErrorKey = null);
