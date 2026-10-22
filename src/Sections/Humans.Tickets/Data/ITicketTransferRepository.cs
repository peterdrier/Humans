using Humans.Base.Interfaces.Repositories;
using Humans.Tickets.Domain;

namespace Humans.Tickets.Data;


internal interface ITicketTransferRepository : IRepository
{
    Task<TicketTransferRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRequest>> GetBySenderAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRequest>> GetByStatusAsync(TicketTransferStatus status, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);

    Task AddAsync(TicketTransferRequest request, CancellationToken ct = default);

    Task UpdateAsync(TicketTransferRequest request, CancellationToken ct = default);

    Task ReassignUserAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);

    /// <summary>
    /// Tombstones the receiver snapshot and clears the sender's reason and the admin notes on every request naming
    /// the user. The row stays: it is the ticket's chain of custody.
    /// </summary>
    Task<int> ErasePiiForUserAsync(Guid userId, CancellationToken ct = default);
}
