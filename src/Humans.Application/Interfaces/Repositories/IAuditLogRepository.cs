using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Audit Log section's tables: <c>audit_log</c>. The only
/// non-test file that writes to <c>DbContext.AuditLogEntries</c> after the
/// Audit Log migration lands.
/// </summary>
/// <remarks>
/// <para>
/// <c>audit_log</c> is append-only per design-rules §12 — only
/// <see cref="AddAsync"/> is exposed; there are no <c>UpdateAsync</c>,
/// <c>DeleteAsync</c>, <c>RemoveAsync</c>, or similar methods. New state =
/// new row.
/// </para>
/// <para>
/// The repository also exposes narrow cross-table lookups (<see cref="GetUserDisplayNamesAsync"/>,
/// <see cref="GetTeamNamesAsync"/>) used by the audit log UI to resolve
/// display data for actor/subject ids. These are read-only and flow back
/// through the service so controllers never query other domains' tables
/// directly.
/// </para>
/// <para>
/// Uses <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>
/// so the repository can be registered as Singleton while
/// <c>HumansDbContext</c> remains Scoped.
/// </para>
/// </remarks>
public interface IAuditLogRepository
{
    // ==========================================================================
    // Writes — append-only
    // ==========================================================================

    /// <summary>
    /// Append a new audit log entry. Persisted immediately (auto-saved).
    /// This is the only write method; there are no updates or deletes.
    /// </summary>
    Task AddAsync(AuditLogEntry entry, CancellationToken ct = default);

    // ==========================================================================
    // Reads — audit log entries
    // ==========================================================================

    /// <summary>
    /// Returns audit entries for a specific Google resource, ordered newest
    /// first, capped at 200.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetByResourceAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>
    /// Returns Google sync audit entries where the user is the related
    /// entity, newest first, capped at 200. Includes the Google resource
    /// navigation for display.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetGoogleSyncByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent audit entries, ordered newest first.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Returns filtered audit entries with pagination, plus total counts.
    /// </summary>
    Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        AuditAction? actionFilter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Returns audit entries where the user is either the primary subject
    /// (EntityType=User, EntityId=userId) or the related entity.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary>
    /// Returns audit entries matching flexible filter criteria. Used by the
    /// shared AuditLog ViewComponent.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<AuditAction>? actions,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every entry affecting the given user (EntityId=userId,
    /// RelatedEntityId=userId, or ActorUserId=userId), newest first. Used by
    /// the GDPR export contributor.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetAllForUserContributorAsync(Guid userId, CancellationToken ct = default);

    // ==========================================================================
    // Cross-table display lookups (read-only)
    // ==========================================================================

    /// <summary>
    /// Batch-load user display names for a set of user IDs.
    /// </summary>
    Task<Dictionary<Guid, string>> GetUserDisplayNamesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Batch-load team names and slugs for a set of team IDs.
    /// </summary>
    Task<Dictionary<Guid, (string Name, string Slug)>> GetTeamNamesAsync(
        IReadOnlyList<Guid> teamIds, CancellationToken ct = default);
}
