using NodaTime;

namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// Orchestrates syncing ticket data from the vendor into local entities.
/// Handles upsert, email matching, and campaign code redemption tracking.
/// </summary>
public interface ITicketSyncService
{
    /// <summary>
    /// Run a full sync cycle: fetch orders and attendees from vendor,
    /// upsert into local DB, auto-match to users by email, and
    /// update campaign grant redemption status.
    /// </summary>
    Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default);

    /// <summary>
    /// Reset the sync state to force a full re-sync on the next sync cycle.
    /// Clears LastSyncAt so all orders are re-fetched.
    /// </summary>
    Task ResetSyncStateForFullResyncAsync();

    /// <summary>
    /// Returns true when the singleton ticket sync state row has
    /// <c>SyncStatus = Error</c>. Used by the notification meter to surface
    /// ticket-sync failures to Admin without letting the Notifications
    /// section read <c>ticket_sync_states</c> directly (design-rules §2c).
    /// </summary>
    Task<bool> IsInErrorStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the current error state plus the most recent error message
    /// recorded on the singleton ticket sync state row. <see cref="ErrorMessage"/>
    /// is null unless <see cref="InError"/> is true. Used by the Admin daily
    /// digest so the digest can render the specific failure message without
    /// reading <c>ticket_sync_states</c> directly (design-rules §2c).
    /// </summary>
    Task<TicketSyncErrorStatus> GetErrorStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Account-merge fold: bulk-moves Tickets-section rows that reference
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>.
    /// Re-FKs <c>ticket_orders.MatchedUserId</c> and
    /// <c>ticket_attendees.MatchedUserId</c> — tickets are unique per
    /// purchase, so no dedup is needed (plain re-FK). Invalidates ticket
    /// caches so dashboard / coverage / who-hasn't-bought reflect the move.
    /// The <paramref name="updatedAt"/> parameter is accepted for signature
    /// parity with other <c>Reassign…ToUserAsync</c> methods across the
    /// merge fold but is <b>unused</b> — neither <c>TicketOrder</c> nor
    /// <c>TicketAttendee</c> carries a generic <c>UpdatedAt</c> column
    /// (only <c>SyncedAt</c>, owned by the vendor-sync pipeline).
    /// Implementations explicitly discard the value.
    /// Returns the count of <c>ticket_attendees</c> rows ultimately
    /// attributed to <paramref name="targetUserId"/>. Called only by
    /// <c>AccountMergeService.AcceptAsync</c>.
    /// </summary>
    Task<int> ReassignToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}

/// <summary>
/// Ticket sync error state shape used by the Admin daily digest.
/// </summary>
public record TicketSyncErrorStatus(bool InError, string? ErrorMessage);

/// <summary>Summary of a sync operation.</summary>
public record TicketSyncResult(
    int OrdersSynced,
    int AttendeesSynced,
    int OrdersMatched,
    int AttendeesMatched,
    int CodesRedeemed);
