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

    /// <summary>
    /// Fetch gate check-ins, optionally since a given timestamp. Check-in is a
    /// vendor resource distinct from issued-ticket status — TicketTailor records
    /// it under <c>/check_ins</c>, not by flipping a ticket's status. <paramref
    /// name="since"/> filters by record creation/upload time (not the scan time),
    /// so scans uploaded late by an offline scanner are not missed. Issue
    /// nobodies-collective/Humans#736.
    /// </summary>
    Task<IReadOnlyList<VendorCheckInDto>> GetCheckInsAsync(
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
}
