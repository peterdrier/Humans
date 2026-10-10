using Humans.Base.Constants;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Issues.Contracts;

/// <summary>
/// Issues' triage surface for the machine API behind <c>/api/backdoor/issues</c>: read the
/// queue and one issue's thread, file an issue, comment, and move status, assignee, section
/// or the linked GitHub issue.
/// </summary>
/// <remarks>
/// <para>
/// Every member takes an <see cref="IssueViewer"/>: the queue is filtered to what that person
/// may see, and every per-item read and mutation is refused unless they may reach that issue.
/// The rule lives in the service, so both doors — the browser and a Backdoor key — enforce it
/// identically and a key reaches exactly as far as its holder does in the browser.
/// </para>
/// <para>
/// Authority and attribution are separate on purpose. The viewer says who may act; the
/// <c>actorUserId</c> / <c>senderUserId</c> arguments say whose name goes on the audit entry.
/// They are the same person at both doors today, and <see cref="CreateIssueAsync"/> is the
/// case that shows why the pair exists: an agent may file an issue on someone else's behalf.
/// </para>
/// </remarks>
public interface IIssueTriage : IApplicationService
{
    Task<IReadOnlyList<IssueListSnapshot>> GetIssueListAsync(
        IssueListFilter filter,
        IssueViewer viewer,
        CancellationToken ct = default);

    /// <summary>
    /// The issue, or null when it does not exist <em>or</em> <paramref name="viewer"/> may not
    /// see it — deliberately indistinguishable, so an id is not an oracle for issues outside
    /// the caller's queue.
    /// </summary>
    Task<IssueDetail?> GetIssueByIdAsync(Guid id, IssueViewer viewer, CancellationToken ct = default);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the issue is gone or
    /// <paramref name="viewer"/> may not see it — again indistinguishable.
    /// </summary>
    Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(
        Guid issueId, IssueViewer viewer, CancellationToken ct = default);

    /// <summary>
    /// Files an issue on behalf of <paramref name="reporterUserId"/> and returns its id.
    /// The screenshot and browser-context fields the in-app reporter captures have no
    /// machine equivalent and are deliberately absent.
    /// </summary>
    /// <param name="reporterUserId">The human whose issue this is and who appears as its reporter.</param>
    /// <param name="category">The issue category.</param>
    /// <param name="title">The issue's short title.</param>
    /// <param name="description">The issue's detailed description.</param>
    /// <param name="section">The owning app section, when known.</param>
    /// <param name="dueDate">The requested due date, if any.</param>
    /// <param name="actorUserId">
    /// The human who actually filed it — the key owner, who need not be the reporter. Recorded
    /// as an <c>IssueCreated</c> audit entry, which is the only durable record of the
    /// distinction: the issue row itself carries the reporter alone.
    /// </param>
    /// <param name="ct">Cancellation token for the operation.</param>
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
    /// Posts a comment. Reporter status is derived from <paramref name="senderUserId"/> —
    /// a reporter's comment on a terminal issue auto-reopens it, whichever door it came
    /// through. Open to a handler or the reporter; <paramref name="resolveOnPost"/> is
    /// honoured for handlers only. A missing result covers both a gone issue and one outside
    /// the viewer's reach. Unexpected comment-write failures propagate; a failed resolution
    /// returns no comment and preserves the missing classification.
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
/// Who is asking, as both doors into <see cref="IIssueTriage"/> see them: the browsing user's
/// own id and claims, or the human a Backdoor key belongs to and the roles that key resolved.
/// </summary>
/// <remarks>
/// Carries who the caller *is*, never what they are allowed to do: there is no privilege flag
/// here for a caller to assert, per <c>memory/code/authorization-conventions.md</c>. Admin is
/// derived from the roles rather than passed alongside them, so the two cannot disagree and a
/// caller cannot claim admin reach it does not hold a claim for.
/// </remarks>
/// <param name="UserId">The person acting. Reaches their own reported issues whatever their roles.</param>
/// <param name="Roles">Their active role names, as they appear in <c>ClaimTypes.Role</c>.</param>
public sealed record IssueViewer(Guid UserId, IReadOnlyList<string> Roles)
{
    /// <summary>Admin reaches every issue — a role like any other, not a separate input.</summary>
    public bool IsAdmin => Roles.Contains(RoleNames.Admin, StringComparer.Ordinal);
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

/// <summary>A triage mutation outcome; dependency failures carry a safe message, not diagnostics.</summary>
public sealed record IssueMutationResult(bool Succeeded, bool NotFound, string? ErrorMessage, bool Rejected = false)
{
    public static IssueMutationResult Success() => new(true, false, null);
    public static IssueMutationResult Missing(string message) => new(false, true, message);
    public static IssueMutationResult Refused(string message) => new(false, false, message, true);
    public static IssueMutationResult Failed(string message) => new(false, false, message);
}

/// <summary>A posted comment, an explicit missing outcome, or no comment when resolution failed.</summary>
public sealed record IssueCommentResult(IssueCommentInfo? Comment, bool NotFound = false);
