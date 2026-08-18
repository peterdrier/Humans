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
/// <b>Placement (nobodies-collective/Humans#866, G5 lane 4b-2h).</b> This lives in
/// <c>Humans.AuditLog</c>, not in the <c>Humans.AuditLog.Contracts</c> leaf and no longer in
/// <c>Humans.Application</c>. Peter's 2026-08-14 Base-floor decision: a former Base resident
/// that names another section's read interface moves to its own section, and Base gets no
/// <c>Humans.Teams.Contracts</c> reference to keep it. Resolution injects
/// <c>IUserServiceRead</c>, <c>ITeamServiceRead</c> and <c>ITeamResourceService</c>, so
/// <c>Humans.AuditLog</c> takes those three contracts leaves — legal and normal at end state.
/// </para>
/// <para>
/// This section project's <c>Contracts/</c> folder, not the <c>Humans.AuditLog.Contracts</c>
/// leaf project: every consumer (Shell's <c>AdminController</c>, the section's own
/// <c>AuditLogController</c> and <c>AuditLogViewComponent</c>, and the <c>Humans.Agent</c>,
/// <c>Humans.Monitor</c> and <c>Humans.Users</c> sections) can take a
/// <c>ProjectReference</c> on <c>Humans.AuditLog</c> directly. A leaf member needs an
/// out-of-section consumer that cannot — no Base project names this — and the leaf must stay
/// reachable from Base without a cycle. Both projects share the
/// <c>Humans.AuditLog.Contracts</c> namespace, as Shifts and Tickets already do.
/// </para>
/// </remarks>
public interface IAuditViewerService : IApplicationService
{
    /// <summary>Most recent audit events, resolved.</summary>
    Task<IReadOnlyList<AuditEvent>> GetRecentAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Audit events involving <paramref name="userId"/> as either the actor
    /// or the subject. Mirrors the merge-tombstone-following semantics of
    /// <c>IAuditLogReader.GetByUserAsync</c>.
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary>
    /// Audit events for a specific Google resource — e.g. the per-resource
    /// sync audit page in the Google integration UI.
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> GetForResourceAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>
    /// Google-sync audit events for a user (chain-followed across merge
    /// tombstones). Scoped to entries written via
    /// <see cref="IAuditLogService.LogGoogleSyncAsync"/>.
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> GetGoogleSyncForUserAsync(Guid userId, CancellationToken ct = default);

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
