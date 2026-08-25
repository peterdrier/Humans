using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Issues.Contracts;

/// <summary>
/// Issues' triage surface for the machine API behind <c>/api/backdoor/issues</c>: read the
/// queue and one issue's thread, file an issue, comment, and move status, assignee, section
/// or the linked GitHub issue.
/// </summary>
/// <remarks>
/// Every mutation takes the acting user. The Backdoor filter resolves the presented key to
/// its owner and passes that id, so an agent's status change is attributed to a person in the
/// audit thread instead of appearing as an anonymous write.
/// </remarks>
public interface IIssueTriage : IApplicationService
{
    Task<IReadOnlyList<IssueListSnapshot>> GetIssueListAsync(
        IssueListFilter filter,
        Guid viewerUserId,
        IReadOnlyList<string> viewerRoles,
        bool viewerIsAdmin,
        CancellationToken ct = default);

    Task<IssueDetail?> GetIssueByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(Guid issueId, CancellationToken ct = default);

    /// <summary>
    /// Files an issue on behalf of <paramref name="reporterUserId"/> and returns its id.
    /// The screenshot and browser-context fields the in-app reporter captures have no
    /// machine equivalent and are deliberately absent.
    /// </summary>
    /// <param name="actorUserId">
    /// The human who actually filed it — the key owner, who need not be the reporter. Recorded
    /// as an <c>IssueCreated</c> audit entry, which is the only durable record of the
    /// distinction: the issue row itself carries the reporter alone.
    /// </param>
    Task<Guid> CreateIssueAsync(
        Guid reporterUserId,
        IssueCategory category,
        string title,
        string description,
        string? section,
        LocalDate? dueDate = null,
        Guid? actorUserId = null,
        CancellationToken ct = default);

    /// <summary>Posts a comment. Throws <see cref="InvalidOperationException"/> when the issue is gone.</summary>
    Task<IssueCommentInfo> PostCommentAsync(
        Guid issueId, Guid? senderUserId, string content,
        bool senderIsReporter, bool resolveOnPost = false, CancellationToken ct = default);

    Task UpdateStatusAsync(
        Guid issueId, IssueStatus newStatus, Guid? actorUserId, CancellationToken ct = default);

    Task UpdateAssigneeAsync(
        Guid issueId, Guid? newAssigneeUserId, Guid? actorUserId, CancellationToken ct = default);

    Task UpdateSectionAsync(
        Guid issueId, string? newSection, Guid? actorUserId, CancellationToken ct = default);

    Task SetGitHubIssueNumberAsync(
        Guid issueId, int? githubIssueNumber, Guid? actorUserId, CancellationToken ct = default);
}

/// <summary>A freshly-posted comment, as much of it as a caller needs to echo back.</summary>
public sealed record IssueCommentInfo(Guid Id, string Content, Instant CreatedAt);

/// <summary>The issue list projection: one row per issue with display data stitched in.</summary>
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
