using Humans.Base.Interfaces.Repositories;
using Humans.GoogleIntegration.Domain;

namespace Humans.GoogleIntegration.Data;

/// <summary>
/// Repository for the section's <c>google_sync_log</c> table. Append-only —
/// no update or delete path is exposed.
/// </summary>
internal interface IGoogleSyncLogRepository : IRepository
{
    Task AddAsync(GoogleSyncLogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Appends a batch in one save. Used by the one-time history migration, which
    /// carries the source audit row's id onto the entry so a re-run is a no-op.
    /// </summary>
    Task AddRangeAsync(IReadOnlyCollection<GoogleSyncLogEntry> entries, CancellationToken ct = default);

    /// <summary>Which of <paramref name="ids"/> are already rows here.</summary>
    Task<IReadOnlySet<Guid>> GetExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Most recent entries for one Google resource.</summary>
    Task<IReadOnlyList<GoogleSyncLogEntry>> GetByResourceAsync(Guid resourceId, CancellationToken ct = default);

    /// <summary>Most recent entries attributed to any of <paramref name="userIds"/>.</summary>
    Task<IReadOnlyList<GoogleSyncLogEntry>> GetByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>Every entry for the GDPR export — uncapped, unlike the display reads.</summary>
    /// <summary>GDPR Art. 17: removes every sync-log row attributed to the given users.</summary>
    Task<int> DeleteByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task<IReadOnlyList<GoogleSyncLogEntry>> GetAllByUserIdsContributorAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}
