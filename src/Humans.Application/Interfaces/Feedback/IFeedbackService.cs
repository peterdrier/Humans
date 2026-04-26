using Humans.Application.Models;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Http;

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

    Task<FeedbackHandoffResult> SubmitFromAgentAsync(
        Guid userId,
        Guid conversationId,
        string summary,
        string topic,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the IDs of feedback reports submitted by the user that are still Open.
    /// Used by the agent snapshot provider.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetOpenFeedbackIdsForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
