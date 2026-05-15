using Microsoft.AspNetCore.Http;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Issues;

public interface IIssuesService : IApplicationService
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
        LocalDate? dueDate,
        IReadOnlyList<string>? reporterRoles,
        CancellationToken ct = default);

    Task<Issue?> GetIssueByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<IssueListSnapshot>> GetIssueListAsync(
        IssueListFilter filter,
        Guid viewerUserId,
        IReadOnlyList<string> viewerRoles,
        bool viewerIsAdmin,
        CancellationToken ct = default);

    Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(Guid issueId, CancellationToken ct = default);

    Task<IssueComment> PostCommentAsync(
        Guid issueId, Guid? senderUserId, string content,
        bool senderIsReporter, CancellationToken ct = default);

    Task<IssueComment> PostCommentAsync(
        Guid issueId, Guid? senderUserId, string content,
        bool senderIsReporter, bool resolveOnPost, CancellationToken ct = default);

    Task UpdateStatusAsync(
        Guid issueId, IssueStatus newStatus, Guid? actorUserId, CancellationToken ct = default);

    Task<IssueMutationResult> UpdateStatusWithResultAsync(
        Guid issueId, IssueStatus newStatus, Guid? actorUserId, CancellationToken ct = default);

    Task UpdateAssigneeAsync(
        Guid issueId, Guid? newAssigneeUserId, Guid? actorUserId, CancellationToken ct = default);

    Task<IssueMutationResult> UpdateAssigneeWithResultAsync(
        Guid issueId, Guid? newAssigneeUserId, Guid? actorUserId, CancellationToken ct = default);

    Task UpdateSectionAsync(
        Guid issueId, string? newSection, Guid? actorUserId, CancellationToken ct = default);

    Task<IssueMutationResult> UpdateSectionWithResultAsync(
        Guid issueId, string? newSection, Guid? actorUserId, CancellationToken ct = default);

    Task SetGitHubIssueNumberAsync(
        Guid issueId, int? githubIssueNumber, Guid? actorUserId, CancellationToken ct = default);

    Task<IssueMutationResult> SetGitHubIssueNumberWithResultAsync(
        Guid issueId, int? githubIssueNumber, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>Count of Open + Triage issues whose section maps to a role the viewer holds, plus their own non-terminal issues.</summary>
    Task<int> GetActionableCountForViewerAsync(
        Guid viewerUserId, IReadOnlyList<string> viewerRoles, bool viewerIsAdmin,
        CancellationToken ct = default);

    Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes issues that entered a terminal state (Resolved / WontFix / Duplicate)
    /// at least 6 months ago, along with their screenshot files. Comments cascade
    /// via the FK. Returns the number of issue rows removed. Invoked by
    /// <c>CleanupIssuesJob</c> on a daily Hangfire schedule.
    /// </summary>
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

public sealed record IssueListSnapshot(
    Guid Id,
    IssueStatus Status,
    IssueCategory Category,
    string? Section,
    string Title,
    string Description,
    string? PageUrl,
    string? UserAgent,
    string? AdditionalContext,
    Guid ReporterUserId,
    string? ReporterDisplayName,
    string? ReporterEmail,
    string? ReporterPreferredLanguage,
    Instant CreatedAt,
    Instant UpdatedAt,
    Instant? ResolvedAt,
    LocalDate? DueDate,
    string? ScreenshotStoragePath,
    int CommentCount,
    Guid? AssigneeUserId,
    string? AssigneeDisplayName,
    int? GitHubIssueNumber);

public sealed record IssueMutationResult(bool Succeeded, bool NotFound, string? ErrorMessage)
{
    public static IssueMutationResult Success() => new(true, false, null);

    public static IssueMutationResult Missing(string message) => new(false, true, message);

    public static IssueMutationResult Failed(string message) => new(false, false, message);
}
