using Humans.Base.Interfaces;

namespace Humans.Feedback.Contracts;

/// <summary>
/// Feedback's triage surface for the machine API behind <c>/api/backdoor/feedback</c>
/// (nobodies-collective/Humans#1128): read the queue, read one report, reply, and change
/// status, assignment or the linked GitHub issue.
/// </summary>
/// <remarks>
/// Feedback is closed to new reports (nobodies-collective/Humans#977) — Issues superseded
/// it — so there is deliberately no create method here either. Every mutation takes the
/// acting user: the Backdoor filter resolves the presented key to its owner and passes that
/// id, so an API reply is attributed exactly like one typed in the admin UI.
/// </remarks>
public interface IFeedbackTriage : IApplicationService
{
    Task<IReadOnlyList<FeedbackReportInfo>> GetFeedbackListAsync(
        FeedbackStatus? status = null, FeedbackCategory? category = null,
        Guid? reporterUserId = null, Guid? assignedToUserId = null,
        Guid? assignedToTeamId = null, bool? unassignedOnly = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<FeedbackReportInfo?> GetFeedbackByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Posts an admin reply. Throws <see cref="InvalidOperationException"/> when the report is gone.</summary>
    Task<FeedbackMessageInfo> PostMessageAsync(
        Guid reportId, Guid? senderUserId, string content,
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        Guid id, FeedbackStatus status, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task UpdateAssignmentAsync(
        Guid id, Guid? assignedToUserId, Guid? assignedToTeamId, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task SetGitHubIssueNumberAsync(
        Guid id, int? issueNumber, Guid? actorUserId,
        CancellationToken cancellationToken = default);
}
