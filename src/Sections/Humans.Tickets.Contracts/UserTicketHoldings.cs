using NodaTime;

namespace Humans.Tickets.Contracts;

public record UserTicketOrderSummary(
    string BuyerName,
    Instant PurchasedAt,
    int AttendeeCount,
    decimal TotalAmount,
    string Currency);

public record UserTicketHoldings(
    int OrderCount,
    IReadOnlyList<UserTicketHoldingRow> Tickets,
    bool HasCurrentEventTicket = false,
    int TicketCount = 0,
    Instant? PostEventHoldDate = null)
{
    public IReadOnlyList<UserTicketOrderSummary> OrderSummaries { get; init; } =
        Array.Empty<UserTicketOrderSummary>();

    public IReadOnlyList<Guid> OpenTicketOrderIds { get; init; } =
        Array.Empty<Guid>();

    public bool HasTicketAttendeeMatch => OrderCount > 0 || Tickets.Count > 0;
}

/// <summary>A gate scan keeps <paramref name="Status"/> at Valid; <paramref name="CheckedInAt"/> is what blocks sending a used ticket.</summary>
public record UserTicketHoldingRow(
    Guid AttendeeId,
    string AttendeeName,
    string? AttendeeEmail,
    string VendorTicketId,
    string TicketTypeName,
    TicketAttendeeStatus Status,
    bool HasPendingOutgoingTransfer = false,
    Guid? PendingTransferRequestId = null,
    Instant? CheckedInAt = null);
