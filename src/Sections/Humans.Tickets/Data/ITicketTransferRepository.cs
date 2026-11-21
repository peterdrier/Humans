using Humans.Base.Interfaces.Repositories;
using Humans.Tickets.Domain;

namespace Humans.Tickets.Data;

/// <summary>
/// The retired transfer-request table: nothing creates requests any more, but its 2026 rows stay. Approved ones still
/// name who a voided ticket went to, and account merge and GDPR erasure still reach every row.
/// </summary>
internal interface ITicketTransferRepository : IRepository
{
    Task<IReadOnlyList<TicketTransferRequest>> GetApprovedAsync(CancellationToken ct = default);

    Task ReassignUserAsync(Guid sourceUserId, Guid targetUserId, CancellationToken ct = default);

    /// <summary>
    /// Tombstones the receiver snapshot and clears the sender's reason and the admin notes on every request naming
    /// the user.
    /// </summary>
    Task<int> ErasePiiForUserAsync(Guid userId, CancellationToken ct = default);
}
