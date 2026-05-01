using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Http;
using NodaTime;

namespace Humans.Application.Interfaces.Feedback;

public interface IFeedbackService
{
    Task<FeedbackReport> SubmitFeedbackAsync(
        Guid userId, FeedbackCategory category, string description,
        string pageUrl, string? userAgent, string? additionalContext,
        IFormFile? screenshot, CancellationToken cancellationToken = default);

    Task<FeedbackReport?> GetFeedbackByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedbackReport>> GetFeedbackListAsync(
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

    Task<FeedbackMessage> PostMessageAsync(
        Guid reportId, Guid? senderUserId, string content, bool isAdmin,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedbackMessage>> GetMessagesAsync(
        Guid reportId, CancellationToken cancellationToken = default);

    Task UpdateAssignmentAsync(
        Guid id, Guid? assignedToUserId, Guid? assignedToTeamId, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task<int> GetActionableCountAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<(Guid UserId, string DisplayName, int Count)>> GetDistinctReportersAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Account-merge fold: bulk-moves feedback authorship from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>.
    /// Re-FKs <c>FeedbackReport.UserId</c> (reporter) and
    /// <c>FeedbackMessage.SenderUserId</c> (message author) — authorship
    /// transfers because all surviving identity is the target's. Plain re-FK,
    /// no dedup: reports and messages are unique events. Stamps
    /// <c>UpdatedAt</c> on each moved report. Actor/audit FKs on the staff
    /// side (<c>AssignedToUserId</c>, <c>ResolvedByUserId</c>) are out of
    /// scope — those are audit pointers and stay at source per the fold
    /// pattern. Returns the total count of report + message rows attributed
    /// to <paramref name="targetUserId"/> after the move. Called only by
    /// <c>AccountMergeService.AcceptAsync</c>.
    /// </summary>
    Task<int> ReassignToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
