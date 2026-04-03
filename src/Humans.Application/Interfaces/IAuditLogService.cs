using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

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
    /// Logs an action performed by a human actor.
    /// </summary>
    Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    /// <summary>
    /// Logs a Google sync action with structured detail fields.
    /// </summary>
    Task LogGoogleSyncAsync(AuditAction action, Guid resourceId,
        string description, string jobName,
        string userEmail, string role, GoogleSyncSource source, bool success,
        string? errorMessage = null,
        Guid? relatedEntityId = null, string? relatedEntityType = null);

    /// <summary>
    /// Gets audit entries for a specific Google resource.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetByResourceAsync(Guid resourceId);

    /// <summary>
    /// Gets Google sync audit entries for a specific user.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetGoogleSyncByUserAsync(Guid userId);

    /// <summary>
    /// Gets the most recent audit log entries.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Gets filtered audit log entries with pagination.
    /// </summary>
    Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Gets audit entries where the user is either the primary or related entity.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary>
    /// Gets audit entries matching flexible filter criteria.
    /// Used by the shared AuditLog ViewComponent for rendering audit history on any page.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default);
}
