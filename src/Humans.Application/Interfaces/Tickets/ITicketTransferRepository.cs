using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Tickets;

public interface ITicketTransferRepository
{
    Task<TicketTransferRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<TicketTransferRequest?> GetPendingForAttendeeAsync(Guid attendeeId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRequest>> GetByRequesterAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<TicketTransferRequest>> GetByStatusAsync(TicketTransferStatus status, CancellationToken ct = default);

    Task<int> CountPendingAsync(CancellationToken ct = default);

    Task AddAsync(TicketTransferRequest request, CancellationToken ct = default);

    Task UpdateAsync(TicketTransferRequest request, CancellationToken ct = default);

    /// <summary>
    /// Repoint <c>RequesterUserId</c> and <c>RecipientUserId</c> from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>.
    /// Called from the account-merge path so the merged user inherits any
    /// transfer requests they were involved in.
    /// </summary>
    Task ReassignUserAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);
}
