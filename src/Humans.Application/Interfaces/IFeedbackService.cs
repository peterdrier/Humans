using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace Humans.Application.Interfaces;

public interface IFeedbackService
{
    Task<FeedbackReport> SubmitFeedbackAsync(
        Guid userId, FeedbackCategory category, string description,
        string pageUrl, string? userAgent, IFormFile? screenshot,
        CancellationToken cancellationToken = default);

    Task<FeedbackReport?> GetFeedbackByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedbackReport>> GetFeedbackListAsync(
        FeedbackStatus? status = null, FeedbackCategory? category = null,
        int limit = 50, CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        Guid id, FeedbackStatus status, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task UpdateAdminNotesAsync(
        Guid id, string? notes, CancellationToken cancellationToken = default);

    Task SetGitHubIssueNumberAsync(
        Guid id, int? issueNumber, CancellationToken cancellationToken = default);

    Task SendResponseAsync(
        Guid id, string message, Guid? actorUserId,
        CancellationToken cancellationToken = default);
}
