using Humans.Base.Interfaces.Repositories;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Domain;
using NodaTime;

namespace Humans.GoogleIntegration.Data;

/// <summary>Append-only: no update or delete path.</summary>
internal interface IGoogleSyncLogRepository : IRepository
{
    Task AddAsync(GoogleSyncLogEntry entry, CancellationToken ct = default);

    /// <summary>The history migration keeps each audit row's id here, so a re-run adds nothing.</summary>
    Task AddRangeAsync(IReadOnlyCollection<GoogleSyncLogEntry> entries, CancellationToken ct = default);

    Task<IReadOnlySet<Guid>> GetExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    Task<IReadOnlyList<GoogleSyncLogEntry>> GetByResourceAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>
    /// Newest first, at most <paramref name="limit"/>. Null filters match all; <paramref name="from"/> is inclusive,
    /// <paramref name="to"/> exclusive. <paramref name="unlinkedDescriptionContains"/> also matches unlinked rows by description.
    /// </summary>
    Task<IReadOnlyList<GoogleSyncLogEntry>> GetRecentAsync(
        Instant? from, Instant? to, GoogleSyncLogAction? action, GoogleSyncSource? source, bool? success,
        IReadOnlyCollection<Guid>? resourceIds, int limit, CancellationToken ct = default,
        string? unlinkedDescriptionContains = null);

    Task<IReadOnlyList<Guid>> GetResourceIdsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<GoogleSyncLogEntry>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<int> DeleteByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>For the GDPR export: uncapped, unlike the display reads.</summary>
    Task<IReadOnlyList<GoogleSyncLogEntry>> GetAllByUserIdsContributorAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}
