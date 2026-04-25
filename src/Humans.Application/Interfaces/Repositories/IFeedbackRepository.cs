using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Feedback section's tables: <c>feedback_reports</c> and
/// <c>feedback_messages</c>. The only non-test file that writes to these
/// DbSets after the Feedback migration lands.
/// </summary>
/// <remarks>
/// Reads never <c>.Include()</c> cross-domain navigation properties
/// (<c>FeedbackReport.User</c>, <c>FeedbackReport.ResolvedByUser</c>,
/// <c>FeedbackReport.AssignedToUser</c>, <c>FeedbackReport.AssignedToTeam</c>,
/// <c>FeedbackMessage.SenderUser</c>). Callers in the Application layer
/// stitch display data from <c>IUserService</c>, <c>IUserEmailService</c>, and
/// <c>ITeamService</c>.
///
/// Feedback is admin-review-only and low-traffic. The repository uses the
/// Singleton + <c>IDbContextFactory</c> pattern so each method owns its own
/// <c>HumansDbContext</c> lifetime.
/// </remarks>
public interface IFeedbackRepository
{
    // ==========================================================================
    // Reads
    // ==========================================================================

    /// <summary>
    /// Loads a single feedback report by id, including its aggregate-local
    /// <c>Messages</c> collection ordered by <c>CreatedAt</c>. Read-only
    /// (AsNoTracking). Cross-domain navs are NOT populated. Returns null if
    /// the report does not exist.
    /// </summary>
    Task<FeedbackReport?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Loads a feedback report by id (no navigation included) for mutation
    /// via <see cref="SaveTrackedReportAsync"/> or
    /// <see cref="AddMessageAndSaveReportAsync"/>. Returns null if not found.
    /// </summary>
    Task<FeedbackReport?> FindForMutationAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Filtered list of feedback reports, with their aggregate-local
    /// <c>Messages</c> collection (callers use <c>Messages.Count</c> only).
    /// Ordered by CreatedAt descending, limited to <paramref name="limit"/>.
    /// Read-only (AsNoTracking).
    /// </summary>
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
    /// Returns all messages for a report, ordered by CreatedAt ascending.
    /// Read-only (AsNoTracking). Cross-domain navs are NOT populated.
    /// </summary>
    Task<IReadOnlyList<FeedbackMessage>> GetMessagesAsync(
        Guid reportId, CancellationToken ct = default);

    /// <summary>
    /// Counts reports that are "actionable" — open with no admin reply, or
    /// with a reporter message more recent than the last admin message —
    /// excluding Resolved and WontFix.
    /// </summary>
    Task<int> GetActionableCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns one entry per distinct reporter user with their report count.
    /// Ordering by display name is the service's responsibility since it
    /// resolves display names from <c>IUserService</c>.
    /// </summary>
    Task<IReadOnlyList<(Guid UserId, int Count)>> GetReporterCountsAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Returns all feedback reports authored by the given user, ordered by
    /// CreatedAt descending, with aggregate-local messages included for GDPR
    /// export. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<FeedbackReport>> GetForUserExportAsync(
        Guid userId, CancellationToken ct = default);

    // ==========================================================================
    // Writes
    // ==========================================================================

    /// <summary>
    /// Persists a new feedback report. Commits immediately.
    /// </summary>
    Task AddReportAsync(FeedbackReport report, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to a tracked report (obtained via <see cref="FindForMutationAsync"/>).
    /// </summary>
    Task SaveTrackedReportAsync(FeedbackReport report, CancellationToken ct = default);

    /// <summary>
    /// Stages a new message and commits it together with the tracked
    /// <paramref name="report"/> that the caller has mutated
    /// (last-message timestamps, <c>UpdatedAt</c>) in a single transaction.
    /// </summary>
    Task AddMessageAndSaveReportAsync(
        FeedbackMessage message, FeedbackReport report, CancellationToken ct = default);
}
