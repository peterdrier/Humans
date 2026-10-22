using Humans.Base.Interfaces;

namespace Humans.Feedback.Contracts;

/// <summary>
/// The machine API surface behind <c>/api/backdoor/feedback</c>. Every mutation takes the acting user, so an API reply
/// is attributed exactly like one typed in the admin UI. No create method: Feedback is closed to new reports.
/// </summary>
public interface IFeedbackTriage : IApplicationService
{
    Task<IReadOnlyList<FeedbackReportInfo>> GetFeedbackListAsync(
        FeedbackStatus? status = null, FeedbackCategory? category = null,
        Guid? reporterUserId = null, Guid? assignedToUserId = null,
        Guid? assignedToTeamId = null, bool? unassignedOnly = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<FeedbackReportInfo?> GetFeedbackByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a missing-report result when the report is gone. A pending email handoff shows on the returned message
    /// and is retried automatically.
    /// </summary>
    Task<FeedbackMutationResult> PostMessageAsync(
        Guid reportId, Guid? senderUserId, string content,
        CancellationToken cancellationToken = default);

    Task<FeedbackMutationResult> UpdateStatusAsync(
        Guid id, FeedbackStatus status, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task<FeedbackMutationResult> UpdateAssignmentAsync(
        Guid id, Guid? assignedToUserId, Guid? assignedToTeamId, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task<FeedbackMutationResult> SetGitHubIssueNumberAsync(
        Guid id, int? issueNumber, Guid? actorUserId,
        CancellationToken cancellationToken = default);
}
