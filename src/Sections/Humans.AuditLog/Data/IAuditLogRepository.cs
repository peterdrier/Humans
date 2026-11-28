using Humans.Base.Interfaces.Repositories;
using Humans.AuditLog.Domain;
using Humans.AuditLog.Services;
using NodaTime;

namespace Humans.AuditLog.Data;

internal interface IAuditLogRepository : IRepository
{
    /// <summary>Saves at once, to the table <see cref="AuditLogEntrySnapshot.Kept"/> names.</summary>
    Task AddAsync(AuditLogEntrySnapshot entry, CancellationToken ct = default);

    /// <summary>
    /// Every read covers both sides. <paramref name="actionFilter"/> matches the stored name exactly; <paramref name="alertActions"/> are counted
    /// over the whole log, one entry per name.
    /// </summary>
    Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, IReadOnlyDictionary<string, int> AlertCounts)> GetFilteredAsync(
        string? actionFilter, IReadOnlyList<string> alertActions, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Where a user is the subject (a User entity) or the related entity. For merge chain-follow, pass the target
    /// plus its merged-away ids.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, int count, CancellationToken ct = default);

    /// <summary>
    /// A non-empty <paramref name="userIds"/> matches the actor, the related entity or a User entity.
    /// <paramref name="includeRelated"/> lets <paramref name="entityId"/> match the related entity too.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        IReadOnlyCollection<Guid>? userIds,
        IReadOnlyList<string>? actions,
        int limit,
        bool includeRelated = false,
        CancellationToken ct = default);

    /// <summary>Actor, entity or related entity; newest first. For the GDPR export.</summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetAllForUserIdsContributorAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Temporary side only: the permanent table refuses deletes. Returns the count deleted.</summary>
    Task<int> DeleteTemporaryBeforeAsync(Instant cutoff, CancellationToken ct = default);

    /// <summary>Temporary side only: entries naming this exact user id as actor, entity or related entity.</summary>
    Task<int> DeleteTemporaryForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The ones among <paramref name="ids"/> on the temporary side. Goes with <c>ITemporaryAuditImport</c>.</summary>
    Task<IReadOnlySet<Guid>> GetTemporaryIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Temporary side only, in one save. Goes with <c>ITemporaryAuditImport</c>.</summary>
    Task AddTemporaryRangeAsync(IReadOnlyList<AuditLogEntrySnapshot> entries, CancellationToken ct = default);

    /// <summary>Oldest first, uncapped. Goes with the Google columns.</summary>
    Task<IReadOnlyList<AuditLogEntry>> GetLegacyGoogleSyncEntriesAsync(CancellationToken ct = default);
}
