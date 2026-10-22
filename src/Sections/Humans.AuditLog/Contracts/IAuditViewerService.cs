using Humans.Base.Interfaces;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// The viewer's GUID never appears in rendered text ("You" instead), and actions with no verb mapping render as
/// null so callers can filter them.
/// </summary>
public interface IAuditViewerService : IApplicationService
{
    /// <summary>
    /// Where <paramref name="userId"/> is the subject or a related entity, not merely the actor. Follows merge
    /// tombstones.
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary><paramref name="actionFilter"/> is a case-insensitive <see cref="AuditAction"/> name match.</summary>
    Task<AuditEventPage> GetPageAsync(string? actionFilter, int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEvent>> GetFilteredAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<AuditAction>? actions,
        int limit,
        CancellationToken ct = default);
}

public sealed record AuditEventPage(
    IReadOnlyList<AuditEvent> Items,
    int TotalCount,
    int AnomalyCount);
