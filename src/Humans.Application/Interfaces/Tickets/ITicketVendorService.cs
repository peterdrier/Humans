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

    /// <summary>
    /// Voids an issued ticket. When <paramref name="voidToHold"/> is true, the seat's
    /// allocation is reserved as a hold (not returned to public sale) and the returned
    /// <see cref="VoidIssuedTicketResult.HoldId"/> can be passed to <see cref="IssueTicketAsync"/>
    /// to reissue the same ticket type without racing open inventory. Never refunds or cancels
    /// the parent order. Throws <see cref="TicketVendorWriteException"/> on vendor failure.
    /// </summary>
    Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default);

    /// <summary>
    /// Issues a new ticket. Caller must supply EITHER EventId+TicketTypeId OR HoldId. Note: TT
    /// does NOT associate API-issued tickets with an order (the resulting ticket has
    /// <c>order_id=null</c> and <c>source="api"</c>). Pass the Humans TicketTransferRequest.Id as
    /// <see cref="IssueTicketRequest.ExternalReference"/> so the next sync can re-link the orphan
    /// attendee. Throws <see cref="TicketVendorWriteException"/> on vendor failure.
    /// </summary>
    Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request, CancellationToken ct = default);

    /// <summary>
    /// Record a check-in for an issued ticket at the vendor (TicketTailor
    /// <c>POST /v1/check_ins</c>). A best-effort mirror fired after a gate admit
    /// so the vendor dashboard / vendor check-in app stays consistent — the Gate
    /// section's own <c>gate_scan_events</c> remains the dedupe authority. Safe to retry.
    /// </summary>
    Task CreateCheckInAsync(
        string vendorTicketId, Instant occurredAt, CancellationToken ct = default);
}
