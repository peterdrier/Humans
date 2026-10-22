using NodaTime;

namespace Humans.Feedback.Contracts;

/// <summary>Cross-section display data (reporter, resolver, assignee, team) is already stitched in.</summary>
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
    Instant CreatedAt,
    bool EmailEnqueuePending = false);

/// <summary>Expected triage outcomes. Core persistence failures remain exceptions; committed replies may await email handoff.</summary>
public sealed record FeedbackMutationResult(
    bool Found, string? Rejection = null, FeedbackMessageInfo? Message = null)
{
    public bool Succeeded => Found && Rejection is null;
    public static FeedbackMutationResult Missing { get; } = new(false);
    public static FeedbackMutationResult Success { get; } = new(true);
}
