using Humans.Base.Interfaces;

namespace Humans.Tickets.Contracts;

/// <summary>A caller wanting a derived aggregate gets a property on <see cref="TicketOrderInfo"/>, not a new method.</summary>
public interface ITicketService : IApplicationService
{
    Task<IReadOnlyList<TicketOrderInfo>> GetTicketOrdersAsync(CancellationToken ct = default);

    /// <summary>
    /// Trimmed, case-sensitive, current event only. Blank or unknown codes return null; any ticket status matches.
    /// </summary>
    Task<TicketAttendeeInfo?> FindCurrentEventAttendeeByBarcodeAsync(
        string? barcode, CancellationToken ct = default);

    /// <summary>Orders they bought, plus every ticket they currently hold.</summary>
    Task<UserTicketHoldings> GetUserTicketHoldingsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>All years. A merged-away id resolves to the survivor's records.</summary>
    Task<IReadOnlyList<EventParticipationInfo>> GetParticipationsAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<EventParticipationInfo>> GetAllParticipationsAsync(CancellationToken ct = default);
}
