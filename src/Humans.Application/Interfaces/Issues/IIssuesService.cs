using Microsoft.AspNetCore.Http;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Issues;

public interface IIssuesService
{
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
        CancellationToken ct = default);

    Task<Issue?> GetIssueByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Issue>> GetIssueListAsync(
        IssueListFilter filter,
        Guid viewerUserId,
        IReadOnlyList<string> viewerRoles,
        bool viewerIsAdmin,
        CancellationToken ct = default);

    Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(Guid issueId, CancellationToken ct = default);

    Task<IssueComment> PostCommentAsync(
        Guid issueId, Guid? senderUserId, string content,
        bool senderIsReporter, CancellationToken ct = default);

    Task UpdateStatusAsync(
        Guid issueId, IssueStatus newStatus, Guid? actorUserId, CancellationToken ct = default);

    Task UpdateAssigneeAsync(
        Guid issueId, Guid? newAssigneeUserId, Guid? actorUserId, CancellationToken ct = default);

    Task UpdateSectionAsync(
        Guid issueId, string? newSection, Guid? actorUserId, CancellationToken ct = default);

    Task SetGitHubIssueNumberAsync(
        Guid issueId, int? githubIssueNumber, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>Count of Open + Triage issues whose section maps to a role the viewer holds, plus their own non-terminal issues.</summary>
    Task<int> GetActionableCountForViewerAsync(
        Guid viewerUserId, IReadOnlyList<string> viewerRoles, bool viewerIsAdmin,
        CancellationToken ct = default);

    Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(CancellationToken ct = default);
}
