using NodaTime;
using Humans.Base.Interfaces;

namespace Humans.Tickets.Services;

internal interface ITicketSyncService : IApplicationService
{
    Task<TicketSyncResult> SyncAsync(CancellationToken ct = default);

    /// <summary>True when the singleton sync state is <c>Error</c>.</summary>
    Task<bool> IsInErrorStateAsync(CancellationToken ct = default);

    Task ResetSyncStateForFullResyncAsync();

    /// <summary>Whether the recurring sync job runs; the admin sync buttons ignore it.</summary>
    Task<bool> IsScheduledSyncEnabledAsync(CancellationToken ct = default);

    Task SetScheduledSyncEnabledAsync(bool enabled, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Days after an event's strike that a ticket holder's deletion waits, and after which each platform erases the
    /// event's personal data.
    /// </summary>
    Task<int> GetPostEventHoldDaysAsync(CancellationToken ct = default);

    Task SetPostEventHoldDaysAsync(int days, Guid actorUserId, CancellationToken ct = default);

    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);
}

internal sealed record TicketSyncResult(
    int OrdersSynced,
    int AttendeesSynced,
    int OrdersMatched,
    int AttendeesMatched,
    int CodesRedeemed);
