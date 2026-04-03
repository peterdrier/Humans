namespace Humans.Application.Interfaces;

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
}

/// <summary>Summary of a sync operation.</summary>
public record TicketSyncResult(
    int OrdersSynced,
    int AttendeesSynced,
    int OrdersMatched,
    int AttendeesMatched,
    int CodesRedeemed);
