using Humans.Base.Interfaces.Repositories;
using Humans.AuditLog.Domain;
using Humans.AuditLog.Contracts;

namespace Humans.AuditLog.Data;

internal interface IAuditLogRepository : IRepository
{
    /// <summary>The only write: saves at once.</summary>
    Task AddAsync(AuditLogEntry entry, CancellationToken ct = default);

    Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        AuditAction? actionFilter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Where a user is the subject (a User entity) or the related entity. For merge chain-follow, pass the target
    /// plus its merged-away ids.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, int count, CancellationToken ct = default);

    /// <summary>
    /// A non-empty <paramref name="userIds"/> matches the actor, the related entity or a User entity.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        IReadOnlyCollection<Guid>? userIds,
        IReadOnlyList<AuditAction>? actions,
        int limit,
        CancellationToken ct = default);

    /// <summary>Actor, entity or related entity; newest first. For the GDPR export.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetAllForUserIdsContributorAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType,
        IReadOnlyList<AuditAction> actions,
        CancellationToken ct = default);

    /// <summary>Oldest first, uncapped. Goes with the Google columns.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetLegacyGoogleSyncEntriesAsync(CancellationToken ct = default);
}
