using NodaTime;

namespace Humans.Feedback.Contracts;

/// <summary>
/// A feedback report with its thread, and the cross-section display data
/// (reporter, resolver, assignee, team) already stitched in by the service.
/// On the public surface for <see cref="IFeedbackTriage"/>, which the Backdoor
/// machine API reads (nobodies-collective/Humans#1128).
/// </summary>
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
