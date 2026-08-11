using Humans.Feedback.Domain;
using NodaTime;

namespace Humans.Feedback.Services.Dtos;

/// <summary>
/// A feedback report with its thread, and the cross-section display data
/// (reporter, resolver, assignee, team) already stitched in by the service.
/// Internal: the section's own controllers are the only readers — what leaves
/// the section is <see cref="Contracts.IFeedbackServiceRead"/>, which returns
/// primitives.
/// </summary>
internal sealed record FeedbackReportInfo(
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

internal sealed record FeedbackMessageInfo(
    Guid Id,
    Guid FeedbackReportId,
    Guid? SenderUserId,
    string? SenderName,
    string Content,
    Instant CreatedAt);
