using Humans.Base.Enums;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// Temporary read over the six Google-sync columns still parked on <c>audit_log</c>.
/// nobodies-collective/Humans#1083 pointed new writes at <c>google_sync_log</c> and left the
/// history where it was; GoogleIntegration's one-time migration screen copies it forward
/// through this. It is deleted along with the columns.
/// </summary>
/// <remarks>
/// Off <see cref="IAuditLogService"/> on purpose: that contract is the write path plus its
/// two justified reads, and this one is scaffolding with a known end date.
/// </remarks>
public interface ILegacyGoogleSyncAuditReader : IApplicationService
{
    /// <summary>
    /// Every <c>audit_log</c> row carrying a Google resource id, oldest first. Uncapped —
    /// the screen reports on the whole set.
    /// </summary>
    Task<IReadOnlyList<LegacyGoogleSyncAuditRow>> GetLegacyGoogleSyncRowsAsync(CancellationToken ct = default);
}

/// <summary>One <c>audit_log</c> row written by the retired <c>LogGoogleSyncAsync</c> path.</summary>
/// <remarks>
/// Every Google-specific column is nullable in the table, so it is nullable here too — the
/// reader reports what is there and the consumer decides what is mappable.
/// </remarks>
public sealed record LegacyGoogleSyncAuditRow(
    Guid Id,
    AuditAction Action,
    Instant OccurredAt,
    string Description,
    Guid ResourceId,
    Guid? RelatedEntityId,
    string? RelatedEntityType,
    string? UserEmail,
    string? Role,
    GoogleSyncSource? SyncSource,
    bool? Success,
    string? ErrorMessage);
