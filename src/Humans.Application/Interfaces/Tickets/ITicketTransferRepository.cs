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
}
