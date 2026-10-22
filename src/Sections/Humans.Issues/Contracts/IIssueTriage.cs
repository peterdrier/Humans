using Humans.Base.Constants;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Issues.Contracts;

/// <summary>The triage surface behind <c>/api/backdoor/issues</c>.</summary>
/// <remarks>
/// Every member takes an <see cref="IssueViewer"/> and refuses anything outside that person's reach, so a key reaches
/// exactly as far as its holder does in the browser. The viewer says who may act; <c>actorUserId</c> /
/// <c>senderUserId</c> say whose name goes on the audit entry. They differ when an agent files for someone else.
/// </remarks>
public interface IIssueTriage : IApplicationService
{
    Task<IReadOnlyList<IssueListSnapshot>> GetIssueListAsync(
        IssueListFilter filter,
        IssueViewer viewer,
        CancellationToken ct = default);

    /// <summary>
    /// Null when the issue does not exist <em>or</em> <paramref name="viewer"/> may not see it: deliberately
    /// indistinguishable, so an id is not an oracle for issues outside the caller's queue.
    /// </summary>
    Task<IssueDetail?> GetIssueByIdAsync(Guid id, IssueViewer viewer, CancellationToken ct = default);

    /// <summary>
    /// Null when the issue is missing or out of reach (as <see cref="GetIssueByIdAsync"/>), has no screenshot, or its
    /// file is gone.
    /// </summary>
    Task<IssueScreenshot?> GetScreenshotAsync(Guid id, IssueViewer viewer, CancellationToken ct = default);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the issue is gone or out of reach, again
    /// indistinguishable.
    /// </summary>
    Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(
        Guid issueId, IssueViewer viewer, CancellationToken ct = default);

    /// <summary>Returns the new id. Screenshot and browser context have no machine equivalent.</summary>
    /// <remarks>
    /// <paramref name="actorUserId"/> is who actually filed it (the key owner), who need not be the reporter. Its
    /// <c>IssueCreated</c> audit entry is the only record of that: the row carries the reporter alone.
    /// </remarks>
    Task<Guid> CreateIssueAsync(
        Guid reporterUserId,
        IssueCategory category,
        string title,
        string description,
        string? section,
        LocalDate? dueDate = null,
        Guid? actorUserId = null,
        CancellationToken ct = default);

    /// <summary>
    /// A reporter's comment (judged by <paramref name="senderUserId"/>) on a terminal issue reopens it.
    /// <paramref name="resolveOnPost"/> is honoured for handlers only. A missing result covers both a gone issue and
    /// one out of reach. Comment-write failures propagate; a failed resolution returns no comment.
    /// </summary>
    Task<IssueCommentResult> PostCommentAsync(
        Guid issueId, IssueViewer viewer, Guid? senderUserId, string content,
        bool resolveOnPost = false, CancellationToken ct = default);

    Task<IssueMutationResult> UpdateStatusAsync(
        Guid issueId, IssueViewer viewer, IssueStatus newStatus, Guid? actorUserId,
        CancellationToken ct = default);

    Task<IssueMutationResult> UpdateAssigneeAsync(
        Guid issueId, IssueViewer viewer, Guid? newAssigneeUserId, Guid? actorUserId,
        CancellationToken ct = default);

    Task<IssueMutationResult> UpdateSectionAsync(
        Guid issueId, IssueViewer viewer, string? newSection, Guid? actorUserId,
        CancellationToken ct = default);

    Task<IssueMutationResult> SetGitHubIssueNumberAsync(
        Guid issueId, IssueViewer viewer, int? githubIssueNumber, Guid? actorUserId,
        CancellationToken ct = default);
}

/// <summary>
/// Carries who the caller is, never what they may do: there is no privilege flag to assert. Admin is derived from
/// <paramref name="Roles"/>, so the two cannot disagree.
/// </summary>
/// <remarks><paramref name="Roles"/> are as they appear in <c>ClaimTypes.Role</c>.</remarks>
public sealed record IssueViewer(Guid UserId, IReadOnlyList<string> Roles)
{
    public bool IsAdmin => Roles.Contains(RoleNames.Admin, StringComparer.Ordinal);
}

public sealed record IssueCommentInfo(Guid Id, string Content, Instant CreatedAt);

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

/// <summary>Dependency failures carry a safe message, not diagnostics.</summary>
public sealed record IssueMutationResult(bool Succeeded, bool NotFound, string? ErrorMessage, bool Rejected = false,
    string? ErrorKey = null, int? ErrorLimit = null)
{
    public static IssueMutationResult Success() => new(true, false, null);
    public static IssueMutationResult Missing(string message) => new(false, true, message);
    public static IssueMutationResult Refused(string message, string? errorKey = null, int? errorLimit = null) =>
        new(false, false, message, true, errorKey, errorLimit);
    public static IssueMutationResult Failed(string message) => new(false, false, message);
}

/// <summary>A posted comment, an explicit missing outcome, or no comment when resolution failed.</summary>
public sealed record IssueCommentResult(IssueCommentInfo? Comment, bool NotFound = false);

/// <summary>The content type was validated at upload.</summary>
public sealed record IssueScreenshot(byte[] Content, string ContentType);
