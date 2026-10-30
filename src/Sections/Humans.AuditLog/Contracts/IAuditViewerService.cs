using Humans.Base.Interfaces;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// The viewer's GUID never appears in rendered text ("You" instead), and actions with no verb render as null so
/// callers can filter them.
/// </summary>
public interface IAuditViewerService : IApplicationService
{
    /// <summary>
    /// Where <paramref name="userId"/> is the subject or a related entity, not merely the actor. Follows merge
    /// tombstones.
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary><paramref name="actions"/> are stored names, matched exactly; null or empty means every action.</summary>
    Task<IReadOnlyList<AuditEvent>> GetFilteredAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<string>? actions,
        int limit,
        CancellationToken ct = default);
}
