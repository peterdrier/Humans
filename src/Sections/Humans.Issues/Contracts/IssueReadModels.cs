using NodaTime;
using Humans.AuditLog.Contracts;

namespace Humans.Issues.Contracts;

/// <summary>A comment or an audit event.</summary>
public abstract record IssueThreadEvent(Instant At, Guid? ActorUserId, string? ActorDisplayName);

public sealed record IssueCommentEvent(
    Guid CommentId,
    Instant At,
    Guid? ActorUserId,
    string? ActorDisplayName,
    bool ActorIsReporter,
    string Content) : IssueThreadEvent(At, ActorUserId, ActorDisplayName);

public sealed record IssueAuditEvent(
    Instant At,
    Guid? ActorUserId,
    string? ActorDisplayName,
    AuditAction Action,
    string Description) : IssueThreadEvent(At, ActorUserId, ActorDisplayName);

public sealed record IssueListFilter(
    IssueStatus[]? Statuses = null,
    IssueCategory[]? Categories = null,
    string?[]? Sections = null,
    Guid? ReporterUserId = null,
    Guid? AssigneeUserId = null,
    string? SearchText = null,
    int Limit = 100);

/// <summary>Display names are left to the consumer; the thread comes from <c>GetThreadAsync</c>.</summary>
public sealed record IssueDetail(
    Guid Id,
    IssueStatus Status,
    IssueCategory Category,
    string? Section,
    string Title,
    string Description,
    string? PageUrl,
    string? UserAgent,
    string? AdditionalContext,
    string? ScreenshotStoragePath,
    Guid ReporterUserId,
    Guid? AssigneeUserId,
    Guid? ResolvedByUserId,
    int? GitHubIssueNumber,
    LocalDate? DueDate,
    Instant CreatedAt,
    Instant UpdatedAt,
    Instant? ResolvedAt,
    int CommentCount);
