using Humans.Base.Interfaces;

namespace Humans.Audit.Contracts;

/// <summary>
/// The viewer's GUID never appears in rendered text ("You" instead), and actions with no verb render as null so
/// callers can filter them.
/// </summary>
public interface IAuditViewerService : IApplicationService
{
    /// <summary>
    /// Where <paramref name="userId"/> is the subject or the target, not merely the actor. Follows merge
    /// tombstones.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="actions"/> are stored names, matched exactly; null or empty means every action.
    /// <paramref name="includeTargets"/> also matches <paramref name="subjectId"/> as the target, so one call
    /// returns everything logged about it, such as an order's lines and payments.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> GetFilteredAsync(
        string? subjectType,
        Guid? subjectId,
        Guid? userId,
        IReadOnlyList<string>? actions,
        int limit,
        bool includeTargets = false,
        CancellationToken ct = default);
}
