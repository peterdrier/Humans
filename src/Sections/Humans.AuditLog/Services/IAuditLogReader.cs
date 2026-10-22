using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;

namespace Humans.AuditLog.Services;

/// <summary>Kept off <see cref="IAuditLogService"/> so the cross-section contract stays narrow.</summary>
internal interface IAuditLogReader : IApplicationService
{
    Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary>Repeats <see cref="IAuditLogService"/>'s, so the viewer takes one injection.</summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default);
}
