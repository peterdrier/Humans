using Humans.Application.Interfaces;
using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Application.Interfaces.Tickets;

/// <summary>
/// Vendor-agnostic interface for ticket platform operations.
/// Implementations wrap vendor-specific APIs (e.g. TicketTailor).
/// </summary>
public interface ITicketVendorService : IApplicationService
{
    /// <summary>Fetch orders, optionally since a given timestamp.</summary>
    Task<IReadOnlyList<VendorOrderDto>> GetOrdersAsync(
        Instant? since, string eventId, CancellationToken ct = default);

    /// <summary>Fetch issued tickets, optionally since a given timestamp.</summary>
    Task<IReadOnlyList<VendorTicketDto>> GetIssuedTicketsAsync(
        Instant? since, string eventId, CancellationToken ct = default);

    /// <summary>Get high-level event summary (capacity, sold, remaining).</summary>
    Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId, CancellationToken ct = default);

    /// <summary>Generate discount codes via vendor API.</summary>
    Task<IReadOnlyList<string>> GenerateDiscountCodesAsync(
        DiscountCodeSpec spec, CancellationToken ct = default);

    /// <summary>Check redemption status of discount codes.</summary>
    Task<IReadOnlyList<DiscountCodeStatusDto>> GetDiscountCodeUsageAsync(
        IEnumerable<string> codes, CancellationToken ct = default);

    /// <summary>
    /// Voids an issued ticket. When <paramref name="voidToHold"/> is true,
    /// returns a hold id that can be passed to <see cref="IssueTicketAsync"/>
    /// so the seat is reissued without racing against open inventory.
    /// </summary>
    Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default);

    /// <summary>
    /// Issues a new ticket. Caller must supply EITHER EventId+TicketTypeId OR
    /// HoldId. Note: TT does NOT associate API-issued tickets with an order
    /// (the resulting ticket has order_id=null and source="api"). Pass the
    /// Humans TicketTransferRequest.Id as <see cref="IssueTicketRequest.ExternalReference"/>
    /// so the next sync can re-link the orphan attendee.
    /// </summary>
    Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request, CancellationToken ct = default);
}
