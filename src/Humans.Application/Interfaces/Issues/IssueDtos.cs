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
