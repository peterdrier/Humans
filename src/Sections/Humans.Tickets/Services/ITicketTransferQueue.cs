using Humans.Base.Interfaces;

namespace Humans.Tickets.Services;

/// <summary>Internal because only this section's admin nav asks; it moves to the Contracts leaf the day another does.</summary>
internal interface ITicketTransferQueue : IApplicationService
{
    Task<int> CountPendingAsync(CancellationToken ct = default);
}
