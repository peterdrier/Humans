using NodaTime;
using Humans.Base.Interfaces;

namespace Humans.Tickets.Services;

internal interface ITicketSyncService : IApplicationService
{
    Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default);

    /// <summary>True when the singleton sync state is <c>Error</c>.</summary>
    Task<bool> IsInErrorStateAsync(CancellationToken ct = default);

    Task ResetSyncStateForFullResyncAsync();

    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);
}

internal sealed record TicketSyncResult(
    int OrdersSynced,
    int AttendeesSynced,
    int OrdersMatched,
    int AttendeesMatched,
    int CodesRedeemed);
