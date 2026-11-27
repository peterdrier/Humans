using Humans.AuditLog.Contracts;
using NodaTime;

namespace Humans.AuditLog.Services;

/// <summary>
/// One row from either side of the log. <c>Action</c> is the stored name; <see cref="Services.AuditViewerService"/>
/// resolves it to its definition. <c>Kept</c> is the side the row is stored on.
/// </summary>
internal sealed record AuditLogEntrySnapshot(
    Guid Id,
    string Action,
    string EntityType,
    Guid EntityId,
    string Description,
    Instant OccurredAt,
    Guid? ActorUserId,
    Guid? RelatedEntityId,
    string? RelatedEntityType,
    AuditKept Kept);
