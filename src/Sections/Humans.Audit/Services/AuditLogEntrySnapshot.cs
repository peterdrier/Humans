using NodaTime;

namespace Humans.Audit.Services;

/// <summary><c>Action</c> is the stored name; <see cref="AuditViewerService"/> resolves it to its definition.</summary>
internal sealed record AuditLogEntrySnapshot(
    Guid Id,
    string Action,
    string SubjectType,
    Guid SubjectId,
    string Description,
    Instant OccurredAt,
    Guid? ActorUserId,
    Guid? TargetId,
    string? TargetType,
    string? JobName = null);
