using Humans.GoogleIntegration.Contracts;
using Humans.Application.Services.AuditLog;

namespace Humans.Application.Interfaces.AuditLog;

/// <summary>
/// Single owner of the audit-log <em>read+render</em> path. Wraps
/// <see cref="IAuditLogService"/> raw queries with name resolution so every
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
/// <b>Why this stays in <c>Humans.Application</c> after the AuditLog G5 move
/// (nobodies-collective/Humans#866).</b> Resolution needs actor and subject display names
/// and team/resource names, so the implementation injects <c>IUserServiceRead</c>,
/// <c>ITeamServiceRead</c> and <c>ITeamResourceService</c> — Users', Teams' and Google
/// Integration's read surfaces. That makes it a cross-section <em>orchestrator</em>, and
/// AuditLog is a <em>horizontal</em> section: <c>peters-hard-rules.md</c> forbids a
/// horizontal from referencing a vertical, so moving this type into
/// <c>Humans.AuditLog</c> would have put a <c>Humans.Teams.Contracts</c>
/// <c>ProjectReference</c> on the horizontal. The section keeps the append path and the
/// raw entry queries (<c>Humans.AuditLog.Contracts.IAuditLogService</c>); this wraps them.
/// </para>
/// <para>
/// Second consequence, and the reason this split is the cheap one:
/// <c>Humans.UI</c>'s <c>AuditLogViewComponent</c> injects this interface and binds
/// <see cref="AuditEvent"/>. <c>Humans.UI</c> cannot reference a section at any price, so
/// had the read path moved, the component and its ~10 call sites would have had to move
/// with it (Teams' rule: a <c>Humans.UI</c> renderer is a hard floor). Leaving the
/// orchestrator here left the component and every call site untouched.
/// </para>
/// </remarks>
public interface IAuditViewerService : IApplicationService
{
    /// <summary>Most recent audit events, resolved.</summary>
    Task<IReadOnlyList<AuditEvent>> GetRecentAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Audit events involving <paramref name="userId"/> as either the actor
    /// or the subject. Mirrors the merge-tombstone-following semantics of
    /// <see cref="IAuditLogService.GetByUserAsync"/>.
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
    /// <see cref="IAuditLogService.GetFilteredAsync"/> takes — case-insensitive
    /// <see cref="Domain.Enums.AuditAction"/> name match.
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
        IReadOnlyList<Domain.Enums.AuditAction>? actions,
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
