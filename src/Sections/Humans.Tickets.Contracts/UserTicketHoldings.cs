using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Summary of a ticket order for display on user-facing pages.
/// </summary>
public record UserTicketOrderSummary(
    string BuyerName,
    Instant PurchasedAt,
    int AttendeeCount,
    decimal TotalAmount,
    string Currency);

/// <summary>
/// Holdings summary for a single user: orders they bought plus per-ticket rows
/// for every ticket they currently hold.
/// </summary>
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

/// <summary>
/// One ticket held by a user, with enough info for the holdings widget and the transfer
/// wizard to render. <paramref name="CheckedInAt"/> is the gate scan, which keeps
/// <paramref name="Status"/> at <c>Valid</c>; the wizard reads it to refuse sending a
/// ticket already used at the door.
/// </summary>
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
