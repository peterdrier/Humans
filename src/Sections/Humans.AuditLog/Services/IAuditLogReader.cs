using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;

namespace Humans.AuditLog.Services;

/// <summary>Kept off <see cref="IAuditLogService"/> so the cross-section contract stays narrow.</summary>
internal interface IAuditLogReader : IApplicationService
{
    /// <summary><paramref name="actionFilter"/> matches a declared name ignoring case.</summary>
    Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, IReadOnlyDictionary<string, int> AlertCounts)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary>Repeats <see cref="IAuditLogService"/>'s by name, so the viewer takes one injection.</summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType,
        Guid? entityId,
        Guid? userId,
        IReadOnlyList<string>? actions,
        int limit,
        CancellationToken ct = default);
}
