using Humans.Base.Interfaces;

namespace Humans.Tickets.Contracts;

/// <summary>
/// The one thing outside the section that asks about ticket transfers: Shell's
/// <c>AdminNavTree</c> badges the pending-review count. The wizard, the admin
/// decision surface and every transfer DTO are internal to Humans.Tickets.
/// </summary>
/// <remarks>
/// A one-method interface beats hanging a count off <see cref="ITicketServiceRead"/>,
/// which would name a queue depth "Read" (Issues' shape). The section's internal
/// <c>ITicketTransferService</c> inherits this.
/// </remarks>
public interface ITicketTransferQueue : IApplicationService
{
    /// <summary>Number of transfer requests awaiting an admin decision.</summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);
}
