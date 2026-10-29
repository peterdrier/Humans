using Humans.Base.Interfaces.Repositories;
using Humans.AuditLog.Domain;

namespace Humans.AuditLog.Data;

internal interface IAuditLogRepository : IRepository
{
    /// <summary>The only write: saves at once.</summary>
    Task AddAsync(AuditLogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="actionFilter"/> matches the stored name exactly; <paramref name="alertActions"/> are counted
    /// over the whole log, one entry per name.
    /// </summary>
    Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, IReadOnlyDictionary<string, int> AlertCounts)> GetFilteredAsync(
        string? actionFilter, IReadOnlyList<string> alertActions, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Where the user is the subject (a User entity) or the related entity.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary>For merge chain-follow: pass the target plus its merged-away ids.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, int count, CancellationToken ct = default);

    /// <summary>
    /// A non-empty <paramref name="userIds"/> matches the actor, the related entity or a User entity, so callers can
    /// pass the target plus its merged-away ids.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        IReadOnlyCollection<Guid>? userIds,
        IReadOnlyList<string>? actions,
        int limit,
        CancellationToken ct = default);

    /// <summary>Actor, entity or related entity; newest first. For the GDPR export.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetAllForUserContributorAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<AuditLogEntry>> GetAllForUserIdsContributorAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType,
        IReadOnlyList<string> actions,
        CancellationToken ct = default);

    /// <summary>Oldest first, uncapped. Goes with the Google columns.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetLegacyGoogleSyncEntriesAsync(CancellationToken ct = default);
}
