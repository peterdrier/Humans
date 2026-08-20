using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;

namespace Humans.AuditLog.Services;

/// <summary>
/// Section-internal audit reads, implemented by <see cref="AuditLogService"/> and consumed
/// only by <see cref="AuditViewerService"/>. Kept off <see cref="IAuditLogService"/> so the
/// cross-section contract stays the write path plus its two justified reads.
/// </summary>
internal interface IAuditLogReader : IApplicationService
{
    /// <summary>
    /// Gets audit entries for a specific Google resource.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByResourceAsync(Guid resourceId);

    /// <summary>
    /// Gets Google sync audit entries for a specific user.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetGoogleSyncByUserAsync(Guid userId);

    /// <summary>
    /// Gets the most recent audit log entries.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetRecentAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Gets filtered audit log entries with pagination.
    /// </summary>
    Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Gets audit entries where the user is either the primary or related entity.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default);

    /// <summary>
    /// Also on <see cref="IAuditLogService"/> — the viewer needs it and takes one injection
    /// rather than two. Same method on <see cref="AuditLogService"/>.
    /// </summary>
    Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default);
}
