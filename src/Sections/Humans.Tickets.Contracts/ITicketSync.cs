using Humans.Base.Interfaces;

namespace Humans.Tickets.Contracts;

/// <summary>
/// The cross-section vendor-sync surface: <c>TicketSyncJob</c> drives the cycle and
/// Notifications' meter provider asks whether the last one failed. The rest of the
/// section's sync surface (the full-resync reset) is internal.
/// </summary>
public interface ITicketSync : IApplicationService
{
    /// <summary>
    /// Run a full sync cycle: fetch orders and attendees from vendor,
    /// upsert into local DB, auto-match to users by email, and
    /// update campaign grant redemption status.
    /// </summary>
    Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true when the singleton ticket sync state row has
    /// <c>SyncStatus = Error</c>. Used by the notification meter to surface
    /// ticket-sync failures to Admin without letting the Notifications
    /// section read <c>ticket_sync_states</c> directly (design-rules §2c).
    /// </summary>
    Task<bool> IsInErrorStateAsync(CancellationToken ct = default);
}

/// <summary>Summary of a sync operation.</summary>
public record TicketSyncResult(
    int OrdersSynced,
    int AttendeesSynced,
    int OrdersMatched,
    int AttendeesMatched,
    int CodesRedeemed);
