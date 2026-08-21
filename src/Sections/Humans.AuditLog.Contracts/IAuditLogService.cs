using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// Audit's cross-section contract: the write path plus the two reads that no
/// other section can get from the AuditLog view component. Section-internal
/// reads live on <c>IAuditLogReader</c> inside <c>Humans.AuditLog</c>.
/// </summary>
/// <remarks>
/// Each <c>LogAsync</c> call persists its entry immediately (auto-saved by the
/// Audit Log repository). The audit log table is append-only per design-rules
/// §12 — only appends are exposed; there is no update or delete path.
/// Persistence is best-effort per §7a: save failures are logged at error level
/// and swallowed so audit problems never break the business operation that
/// invoked them. Call audit <em>after</em> the business save so a business
/// rollback never leaves a ghost audit row.
/// </remarks>
public interface IAuditLogService : IApplicationService
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
    /// Gets audit entries matching flexible filter criteria. The one genuine
    /// cross-section read: <c>IssuesService.GetThreadAsync</c> interleaves audit
    /// events with issue comments into one chronological thread, so it needs the
    /// rows as data and cannot use the AuditLog view component.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct set of <c>AuditLogEntry.EntityId</c> values
    /// across all-time audit entries whose <c>AuditLogEntry.EntityType</c>
    /// matches <paramref name="entityType"/> and whose
    /// <c>AuditLogEntry.Action</c> is one of <paramref name="actions"/>.
    /// Used by orphan-signup reconciliation to find ShiftSignups missing a
    /// creation-event audit row without crossing the AuditLog section boundary
    /// (design-rules §2c).
    /// </summary>
    Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType,
        IReadOnlyList<AuditAction> actions,
        CancellationToken ct = default);
}

public sealed record AuditLogEntrySnapshot(
    Guid Id,
    AuditAction Action,
    string EntityType,
    Guid EntityId,
    string Description,
    Instant OccurredAt,
    Guid? ActorUserId,
    Guid? RelatedEntityId,
    string? RelatedEntityType);
