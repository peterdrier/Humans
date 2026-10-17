using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Cross-section read surface for the Google Integration sync service.
/// External sections inject this interface; it exposes the outbox failure count and Drive
/// probes needed cross-section, never EF entities or mutation methods. The few writes other
/// sections make live on <see cref="IGoogleSyncWrite"/>.
/// See <c>memory/architecture/section-public-contract.md</c>.
/// </summary>
public interface IGoogleSyncService : IApplicationService
{
    /// <summary>
    /// Returns the count of unprocessed Google sync outbox events that have a
    /// non-null <c>LastError</c>. Used by the notification meter to surface
    /// failed sync events to Admin without letting the Notifications section
    /// read <c>google_sync_outbox</c> directly (design-rules §2c).
    /// </summary>
    Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the service account can read the Drive folder
    /// <paramref name="folderId"/>. Lets a section refuse a folder setting that
    /// <see cref="IGoogleSyncWrite.CreateSubfolderAsync"/> would later 404 on.
    /// </summary>
    Task<bool> IsFolderReachableAsync(string folderId, CancellationToken cancellationToken = default);
}

public sealed record GoogleSyncOutboxEventSnapshot(
    Guid Id,
    string EventType,
    Guid TeamId,
    Guid UserId,
    Instant OccurredAt,
    Instant? ProcessedAt,
    int RetryCount,
    string? LastError,
    bool FailedPermanently);
