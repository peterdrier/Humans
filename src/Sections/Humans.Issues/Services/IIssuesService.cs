using Microsoft.AspNetCore.Http;
using NodaTime;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;
using Humans.Issues.Services.Dtos;

namespace Humans.Issues.Services;

/// <summary>
/// The section's own service surface. Internal, not on the contracts leaf: the only
/// members anything outside Issues calls are the two on <see cref="IIssuesServiceRead"/>
/// and <see cref="IIssuesRetention"/>. It survives internalisation at all because
/// <c>IssuesApiControllerTests</c> substitutes it and MA0053 seals the concrete class
/// (design §15 step 5).
/// </summary>
internal interface IIssuesService : IIssuesServiceRead, IIssuesRetention
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
        IReadOnlyList<string>? reporterRoles = null,
        CancellationToken ct = default);

    Task<IssueDetail?> GetIssueByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<IssueListSnapshot>> GetIssueListAsync(
        IssueListFilter filter,
        Guid viewerUserId,
        IReadOnlyList<string> viewerRoles,
        bool viewerIsAdmin,
        CancellationToken ct = default);

    Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(Guid issueId, CancellationToken ct = default);

    Task<IssueComment> PostCommentAsync(
        Guid issueId, Guid? senderUserId, string content,
        bool senderIsReporter, bool resolveOnPost = false, CancellationToken ct = default);

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

    Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(CancellationToken ct = default);
}

internal sealed record IssueListSnapshot(
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

internal sealed record IssueMutationResult(bool Succeeded, bool NotFound, string? ErrorMessage)
{
    public static IssueMutationResult Success() => new(true, false, null);

    public static IssueMutationResult Missing(string message) => new(false, true, message);

    public static IssueMutationResult Failed(string message) => new(false, false, message);
}
