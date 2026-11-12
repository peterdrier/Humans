using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;

namespace Humans.AuditLog.Services;

/// <summary>Raw-snapshot reads; only <see cref="AuditViewerService"/> calls them, and it is the public read surface.</summary>
internal interface IAuditLogReader : IApplicationService
{
    /// <summary><paramref name="actionFilter"/> matches a declared name ignoring case.</summary>
    Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, IReadOnlyDictionary<string, int> AlertCounts)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default);

    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<string>? actions,
        int limit,
        CancellationToken ct = default);
}
