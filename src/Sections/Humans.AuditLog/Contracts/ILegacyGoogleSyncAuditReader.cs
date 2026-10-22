using Humans.Base.Enums;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.AuditLog.Contracts;

/// <summary>
/// Temporary: feeds GoogleIntegration's one-time history migration screen, and goes with the six Google columns
/// on <c>audit_log</c>.
/// </summary>
public interface ILegacyGoogleSyncAuditReader : IApplicationService
{
    /// <summary>Oldest first, uncapped.</summary>
    Task<IReadOnlyList<LegacyGoogleSyncAuditRow>> GetLegacyGoogleSyncRowsAsync(CancellationToken ct = default);
}

/// <summary>Nullable as in the table: the consumer decides what is mappable.</summary>
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
