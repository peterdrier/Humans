using Humans.Feedback.Contracts;
using Humans.Base.Interfaces.Repositories;
using Humans.Feedback.Domain;
using NodaTime;

namespace Humans.Feedback.Data;

internal interface IFeedbackRepository : IRepository
{
    /// <summary>Messages ordered by <c>CreatedAt</c>, no tracking. Null when absent.</summary>
    Task<FeedbackReport?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Loads replies still awaiting durable Email handoff; historical null flags are excluded.</summary>
    Task<IReadOnlyList<FeedbackMessage>> GetPendingReplyEmailsAsync(CancellationToken ct = default);

    /// <summary>Clears only the owned reply's pending flag after handoff; false if absent or already complete.</summary>
    Task<bool> CompleteReplyEmailAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>
    /// No navigations; for <see cref="SaveTrackedReportAsync"/> or <see cref="AddMessageAndSaveReportAsync"/>. Null when
    /// absent.
    /// </summary>
    Task<FeedbackReport?> FindForMutationAsync(Guid id, CancellationToken ct = default);

    /// <summary>Newest first, capped by <paramref name="limit"/>. Callers use <c>Messages.Count</c> only.</summary>
    Task<IReadOnlyList<FeedbackReport>> GetListAsync(
        FeedbackStatus? status,
        FeedbackCategory? category,
        Guid? reporterUserId,
        Guid? assignedToUserId,
        Guid? assignedToTeamId,
        bool? unassignedOnly,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Open with no admin reply, or with a reporter message newer than the last admin message. Excludes Resolved and
    /// WontFix.
    /// </summary>
    Task<int> GetActionableCountAsync(CancellationToken ct = default);

    /// <summary>Unordered: the service sorts by display name.</summary>
    Task<IReadOnlyList<(Guid UserId, int Count)>> GetReporterCountsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Reports filed by the user or containing their replies, with messages, unordered. The export service trims other
    /// people's reports to the user's own replies.
    /// </summary>
    Task<IReadOnlyList<FeedbackReport>> GetForUserExportAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Attaches the report as Modified.</summary>
    Task SaveTrackedReportAsync(FeedbackReport report, CancellationToken ct = default);

    /// <summary>Commits the new message and the caller-mutated <paramref name="report"/> in one SaveChanges.</summary>
    Task AddMessageAndSaveReportAsync(
        FeedbackMessage message, FeedbackReport report, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: hard-deletes the user's own reports (messages cascade) and detaches them from messages and triage
    /// fields on other people's reports, whose text is not theirs to erase.
    /// </summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>One transaction. Plain re-FK: reports and messages are unique events, no dedup.</summary>
    Task ReassignToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
