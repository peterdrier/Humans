using Profiles.Domain.Enums;

namespace Profiles.Application.Interfaces;

/// <summary>
/// Service for recording audit log entries.
/// Entries are added to the DbContext but NOT saved — the caller's SaveChangesAsync
/// persists them atomically with the business operation.
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// Logs an action performed by a background job (no human actor).
    /// </summary>
    Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    /// <summary>
    /// Logs an action performed by a human admin.
    /// </summary>
    Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId, string actorDisplayName,
        Guid? relatedEntityId = null, string? relatedEntityType = null);
}
