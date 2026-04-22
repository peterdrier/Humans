using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Service for recording audit log entries. Each <c>LogAsync</c> call persists
/// its entry immediately (auto-saved by the Audit Log repository). The audit
/// log table is append-only per design-rules §12 — only appends are exposed;
/// there is no update or delete path. Persistence is best-effort per §7a:
/// save failures are logged at error level and swallowed so audit problems
/// never break the business operation that invoked them. Call audit
/// <em>after</em> the business save so a business rollback never leaves a
/// ghost audit row.
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

    /// <summary>
    /// Gets a full audit log page with display name lookups for users and teams.
    /// Used by Board/Admin audit log views to avoid direct DbContext access in controllers.
    /// </summary>
    Task<AuditLogPageResult> GetAuditLogPageAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Batch-loads user display names for a set of user IDs.
    /// </summary>
    Task<Dictionary<Guid, string>> GetUserDisplayNamesAsync(IReadOnlyList<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Batch-loads team names and slugs for a set of team IDs.
    /// </summary>
    Task<Dictionary<Guid, (string Name, string Slug)>> GetTeamNamesAsync(IReadOnlyList<Guid> teamIds, CancellationToken ct = default);
}

/// <summary>
/// Full audit log page with display name dictionaries for rendering.
/// </summary>
public record AuditLogPageResult(
    IReadOnlyList<AuditLogEntry> Items,
    int TotalCount,
    int AnomalyCount,
    Dictionary<Guid, string> UserDisplayNames,
    Dictionary<Guid, (string Name, string Slug)> TeamNames);
