using NodaTime;

namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Cross-section read surface for the Google Integration sync service.
/// External sections inject this interface; it exposes only the outbox read
/// projections needed cross-section, never EF entities or mutation methods.
/// See <c>memory/architecture/section-read-write-split.md</c>.
/// </summary>
public interface IGoogleSyncServiceRead
{
    /// <summary>
    /// Returns the count of unprocessed Google sync outbox events that have a
    /// non-null <c>LastError</c>. Used by the notification meter to surface
    /// failed sync events to Admin without letting the Notifications section
    /// read <c>google_sync_outbox_events</c> directly (design-rules §2c).
    /// </summary>
    Task<int> GetFailedSyncEventCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of outbox events that have not yet been processed
    /// (<c>ProcessedAt == null</c>), for the Admin pending-queue-size gauge.
    /// Added at GoogleIntegration's G5 so <c>HumansMetricsService</c> stops
    /// injecting <c>IGoogleSyncOutboxRepository</c> directly — the repository
    /// moves into the section with its table, and a Base caller reaching a
    /// section's repository is what design-rules §2a/§2c forbid. Finishes the
    /// migration #554 began for <c>NotificationMeterProvider</c>.
    /// </summary>
    Task<int> GetPendingSyncEventCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the most recent Google sync outbox events for the admin
    /// dashboard, ordered newest-first and capped by <paramref name="take"/>.
    /// Keeps <c>google_sync_outbox_events</c> reads inside the owning service
    /// (design-rules §2a/§2c) so callers do not reach past
    /// <see cref="IGoogleSyncServiceRead"/> into the repository directly.
    /// </summary>
    Task<IReadOnlyList<GoogleSyncOutboxEventSnapshot>> GetRecentOutboxEventsAsync(
        int take, CancellationToken cancellationToken = default);
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
