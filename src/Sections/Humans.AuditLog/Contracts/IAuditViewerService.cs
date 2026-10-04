using Humans.Base.Interfaces;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// Single owner of the audit-log <em>read+render</em> path. Wraps
/// <c>IAuditLogReader</c> raw queries with name resolution so every
/// caller — controllers, view components, the agent tool — consumes the
/// same resolved-event shape (<see cref="AuditEvent"/>) rather than
/// re-implementing the query → batch-resolve actor/subject/team-name dance.
/// </summary>
/// <remarks>
/// Reads only. The append path (<see cref="IAuditLogService.LogAsync"/> and
/// friends) stays where it is. Privacy guard: the viewer's GUID never
/// appears in <see cref="AuditEvent.RenderPlainText"/> output (substituted
/// with "You"), and entries whose action has no verb mapping render as
/// <c>null</c> so callers can filter rather than dump raw descriptions.
/// </remarks>
/// <remarks>
/// <para>
/// <b>Placement.</b> This lives in <c>Humans.AuditLog</c>'s <c>Contracts/</c> folder. Peter's 2026-08-14 Base-floor decision: a former Base resident
/// that names another section's read interface moves to its own section. Since
/// nobodies-collective/Humans#1059 it names none: display names arrive through Base's
/// <c>IEntityNameContributor</c> fan-out.
/// </para>
/// </remarks>
public interface IAuditViewerService : IApplicationService
{
    /// <summary>
    /// Audit events where <paramref name="userId"/> is the subject (an entry on
    /// their User row) or a related entity — not merely the actor. Mirrors the
    /// merge-tombstone-following semantics of <c>IAuditLogReader.GetByUserAsync</c>.
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary>
    /// Returns a paged slice of audit events plus aggregate counts (total,
    /// anomalies). Filter is the same string
    /// <c>IAuditLogReader.GetFilteredAsync</c> takes — case-insensitive
    /// <see cref="Humans.AuditLog.Contracts.AuditAction"/> name match.
    /// </summary>
    Task<AuditEventPage> GetPageAsync(string? actionFilter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Audit events matching the same flexible filter shape as
    /// <see cref="IAuditLogService.GetFilteredEntriesAsync"/>. Used by the
    /// shared <c>AuditLogViewComponent</c> to render audit history on any
    /// page (entity-scoped, user-scoped, or action-scoped).
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> GetFilteredAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<AuditAction>? actions,
        int limit,
        CancellationToken ct = default);
}

/// <summary>
/// Paged result of <see cref="IAuditViewerService.GetPageAsync"/>. Carries
/// resolved events (no raw IDs) plus the totals callers need to render
/// pagination controls and anomaly badges.
/// </summary>
public sealed record AuditEventPage(
    IReadOnlyList<AuditEvent> Items,
    int TotalCount,
    int AnomalyCount);
