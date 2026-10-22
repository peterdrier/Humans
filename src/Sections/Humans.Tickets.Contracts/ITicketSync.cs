using Humans.Base.Interfaces;

namespace Humans.Tickets.Contracts;

public interface ITicketSync : IApplicationService
{
    Task<TicketSyncResult> SyncOrdersAndAttendeesAsync(CancellationToken ct = default);

    /// <summary>True when the singleton sync state is <c>Error</c>.</summary>
    Task<bool> IsInErrorStateAsync(CancellationToken ct = default);
}

public record TicketSyncResult(
    int OrdersSynced,
    int AttendeesSynced,
    int OrdersMatched,
    int AttendeesMatched,
    int CodesRedeemed);
