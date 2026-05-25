using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Issues;

/// <summary>One row in the inline thread — either a comment or an audit event.</summary>
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

/// <summary>Filter criteria for the index list query.</summary>
public sealed record IssueListFilter(
    IssueStatus[]? Statuses = null,
    IssueCategory[]? Categories = null,
    string?[]? Sections = null,
    Guid? ReporterUserId = null,
    Guid? AssigneeUserId = null,
    string? SearchText = null,
    int Limit = 100);

public sealed record DistinctReporterRow(Guid UserId, string DisplayName, int Count);

/// <summary>
/// Single-issue projection for the detail view, API get, and resource-based
/// authorization. Display names are resolved by the consumer via
/// <c>IUserService</c> from the user-id fields; the thread is fetched
/// separately via <c>GetThreadAsync</c>.
/// </summary>
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
