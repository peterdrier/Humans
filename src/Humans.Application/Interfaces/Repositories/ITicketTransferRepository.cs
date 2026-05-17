using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Attributes;

namespace Humans.Application.Interfaces.Repositories;

[Section("Tickets")]

public interface ITicketTransferRepository : IRepository
{
    Task<TicketTransferRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRequest>> GetBySenderAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRequest>> GetByStatusAsync(TicketTransferStatus status, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);

    Task AddAsync(TicketTransferRequest request, CancellationToken ct = default);

    Task UpdateAsync(TicketTransferRequest request, CancellationToken ct = default);

    /// <summary>
    /// Repoint <c>SenderUserId</c> and <c>ReceiverUserId</c> from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>.
    /// Called from the account-merge path so the merged user inherits any
    /// transfer requests they were involved in.
    /// </summary>
    Task ReassignUserAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);
}
