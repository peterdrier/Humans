using Humans.Audit.Contracts;
using NodaTime;

namespace Humans.Audit.Services;

/// <summary>
/// One row from either side of the log. <c>Action</c> is the stored name; <see cref="Services.AuditViewerService"/>
/// resolves it to its definition. <c>Kept</c> is the side the row is stored on.
/// </summary>
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
    AuditKept Kept,
    string? JobName = null);
