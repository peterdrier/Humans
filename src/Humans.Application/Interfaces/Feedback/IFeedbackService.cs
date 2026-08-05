using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Feedback;

/// <summary>
/// Feedback is closed to new reports (nobodies-collective/Humans#977) — Issues
/// superseded it. This interface deliberately exposes no report-creation method;
/// what remains is admin-only read and triage over the historical rows.
/// </summary>
public interface IFeedbackService : IApplicationService
{
    Task<FeedbackReportInfo?> GetFeedbackByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedbackReportInfo>> GetFeedbackListAsync(
        FeedbackStatus? status = null, FeedbackCategory? category = null,
        Guid? reporterUserId = null, Guid? assignedToUserId = null,
        Guid? assignedToTeamId = null, bool? unassignedOnly = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        Guid id, FeedbackStatus status, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task SetGitHubIssueNumberAsync(
        Guid id, int? issueNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends an admin reply to a report's thread. Reporters have no route into
    /// Feedback any more, so every message posted from here is an admin reply:
    /// it stamps <c>LastAdminMessageAt</c>, emails the reporter, and notifies them.
    /// </summary>
    Task<FeedbackMessage> PostMessageAsync(
        Guid reportId, Guid? senderUserId, string content,
        CancellationToken cancellationToken = default);

    Task UpdateAssignmentAsync(
        Guid id, Guid? assignedToUserId, Guid? assignedToTeamId, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task<int> GetActionableCountAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(Guid UserId, string DisplayName, int Count)>> GetDistinctReportersAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the IDs of feedback reports submitted by the user that are still Open.
    /// Used by the agent snapshot provider.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOpenFeedbackIdsForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed record FeedbackReportInfo(
    Guid Id,
    Guid UserId,
    FeedbackCategory Category,
    string Description,
    string PageUrl,
    string? UserAgent,
    string? AdditionalContext,
    string? ScreenshotStoragePath,
    FeedbackStatus Status,
    int? GitHubIssueNumber,
    Instant? LastReporterMessageAt,
    Instant? LastAdminMessageAt,
    bool NeedsReply,
    Instant CreatedAt,
    Instant UpdatedAt,
    Instant? ResolvedAt,
    Guid? ResolvedByUserId,
    Guid? AssignedToUserId,
    Guid? AssignedToTeamId,
    string ReporterName,
    string? ReporterEmail,
    string ReporterLanguage,
    string? ResolvedByName,
    string? AssignedToName,
    string? AssignedToTeamName,
    IReadOnlyList<FeedbackMessageInfo> Messages);

public sealed record FeedbackMessageInfo(
    Guid Id,
    Guid FeedbackReportId,
    Guid? SenderUserId,
    string? SenderName,
    string Content,
    Instant CreatedAt);
