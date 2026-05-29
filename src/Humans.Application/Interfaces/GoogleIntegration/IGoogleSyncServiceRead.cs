using NodaTime;

namespace Humans.Application.Interfaces.GoogleIntegration;

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
    string EventType,
    Guid TeamId,
    Guid UserId,
    Instant OccurredAt,
    Instant? ProcessedAt,
    int RetryCount,
    string? LastError,
    bool FailedPermanently);
