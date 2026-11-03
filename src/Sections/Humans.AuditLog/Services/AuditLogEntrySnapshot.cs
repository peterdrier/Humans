using NodaTime;

namespace Humans.AuditLog.Services;

/// <summary><c>Action</c> is the stored name; <see cref="AuditViewerService"/> resolves it to its definition.</summary>
internal sealed record AuditLogEntrySnapshot(
    Guid Id,
    string Action,
    string EntityType,
    Guid EntityId,
    string Description,
    Instant OccurredAt,
    Guid? ActorUserId,
    Guid? RelatedEntityId,
    string? RelatedEntityType);
