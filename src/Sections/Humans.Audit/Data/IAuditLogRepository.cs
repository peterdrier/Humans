using Humans.Base.Interfaces.Repositories;
using Humans.Audit.Domain;

namespace Humans.Audit.Data;

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

    /// <summary>
    /// Where a user is the subject (a User entity) or the target. For merge chain-follow, pass the target
    /// plus its merged-away ids.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, int count, CancellationToken ct = default);

    /// <summary>
    /// A non-empty <paramref name="userIds"/> matches the actor, the target or a User subject.
    /// <paramref name="includeTargets"/> lets <paramref name="subjectId"/> match the target too.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? subjectType,
        Guid? subjectId,
        IReadOnlyCollection<Guid>? userIds,
        IReadOnlyList<string>? actions,
        int limit,
        bool includeTargets = false,
        CancellationToken ct = default);

    /// <summary>Actor, subject or target; newest first. For the GDPR export.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetAllForUserIdsContributorAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Oldest first, uncapped. Goes with the Google columns.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetLegacyGoogleSyncEntriesAsync(CancellationToken ct = default);
}
