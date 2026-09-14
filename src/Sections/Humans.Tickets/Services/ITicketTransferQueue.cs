using Humans.Base.Interfaces;

namespace Humans.Tickets.Services;

/// <summary>
/// Asked about ticket transfers by the section's own admin nav contribution
/// (<c>SectionAdminNav</c>), which badges the pending-review count. The wizard, the admin
/// decision surface and every transfer DTO are likewise internal to Humans.Tickets.
/// </summary>
/// <remarks>
/// A one-method interface beats hanging a count off <c>ITicketServiceRead</c>,
/// which would name a queue depth "Read" (Issues' shape). <see cref="ITicketTransferService"/>
/// inherits this. Internal because no other section asks the question; it moves to the
/// Contracts leaf the day one does.
/// </remarks>
internal interface ITicketTransferQueue : IApplicationService
{
    /// <summary>Number of transfer requests awaiting an admin decision.</summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);
}
