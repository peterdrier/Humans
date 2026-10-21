using NodaTime;
using Humans.Base.Interfaces;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Services;

/// <summary>
/// Orchestrates syncing ticket data from the vendor into local entities.
/// Handles upsert, email matching, and campaign code redemption tracking.
/// </summary>
/// <remarks>
/// The two members Base consumes are on <see cref="ITicketSync"/>; this adds the
/// full-resync reset, which only <c>TicketController</c> drives.
/// </remarks>
internal interface ITicketSyncService : ITicketSync, IApplicationService
{
    /// <summary>
    /// Reset the sync state to force a full re-sync on the next sync cycle.
    /// Clears LastSyncAt so all orders are re-fetched.
    /// </summary>
    Task ResetSyncStateForFullResyncAsync();

    /// <summary>
    /// Account-merge fold: re-FKs ticket orders, attendees and transfer requests from
    /// <paramref name="mergedFromUserId"/> onto <paramref name="mergedToUserId"/>. The caching
    /// decorator fronts this as the section's <c>IUserMerge</c> and evicts both users' holdings.
    /// </summary>
    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);
}
