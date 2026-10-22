using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// The vendor port: a vendor swap is one adapter project deleted and one added. Other sections go through
/// <see cref="ITicketDiscountCodes"/> and <see cref="ITicketVendorMirror"/>; the contracts leaf names none of this.
/// </summary>
public interface ITicketVendorService : IApplicationService
{
    Task<IReadOnlyList<VendorOrderDto>> GetOrdersAsync(
        Instant? since, string eventId, CancellationToken ct = default);

    Task<IReadOnlyList<VendorTicketDto>> GetIssuedTicketsAsync(
        Instant? since, string eventId, CancellationToken ct = default);

    /// <summary>
    /// Check-ins are a vendor resource separate from ticket status. <paramref name="since"/> filters by upload time,
    /// not scan time, so late uploads from an offline scanner are not missed.
    /// </summary>
    Task<IReadOnlyList<VendorCheckInDto>> GetCheckInsAsync(
        Instant? since, string eventId, CancellationToken ct = default);

    Task<VendorEventSummaryDto> GetEventSummaryAsync(
        string eventId, CancellationToken ct = default);

    [ExternalWrite]
    Task<IReadOnlyList<string>> GenerateDiscountCodesAsync(
        DiscountCodeSpec spec, CancellationToken ct = default);

    /// <summary>
    /// With <paramref name="voidToHold"/> the seat is held off-sale, and the returned
    /// <see cref="VoidIssuedTicketResult.HoldId"/> reissues the same ticket type without racing open inventory.
    /// Never refunds or cancels the order. Throws <see cref="TicketVendorWriteException"/> on vendor failure.
    /// </summary>
    [ExternalWrite]
    Task<VoidIssuedTicketResult> VoidIssuedTicketAsync(
        string vendorTicketId, bool voidToHold, CancellationToken ct = default);

    /// <summary>
    /// The vendor links API-issued tickets to no order: pass the transfer request id as
    /// <see cref="IssueTicketRequest.ExternalReference"/> so the next sync re-links the orphan. Throws
    /// <see cref="TicketVendorWriteException"/> on vendor failure.
    /// </summary>
    [ExternalWrite]
    Task<VendorTicketDto> IssueTicketAsync(
        IssueTicketRequest request, CancellationToken ct = default);

    /// <summary>Not idempotent: each call creates a vendor record, so callers must never retry.</summary>
    [ExternalWrite]
    Task CreateCheckInAsync(
        string vendorTicketId, Instant occurredAt, CancellationToken ct = default);
}
