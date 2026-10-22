using NodaTime;

namespace Humans.Tickets.Contracts;

public sealed record TicketStubInfo(
    string AttendeeName,
    string? AttendeeEmail,
    TicketAttendeeStatus Status,
    bool HasPendingTransfer,
    Guid? PendingTransferRequestId,
    LocalDate? EarlyEntryDate)
{
    /// <summary>
    /// The one mapper for a holder's own stubs, so the EE pill can't differ between surfaces.
    /// <paramref name="holderEarlyEntry"/> is the viewer's EE, never the attendee's.
    /// </summary>
    public static TicketStubInfo From(UserTicketHoldingRow row, LocalDate? holderEarlyEntry) =>
        new(row.AttendeeName, row.AttendeeEmail, row.Status,
            row.HasPendingOutgoingTransfer, row.PendingTransferRequestId, holderEarlyEntry);
}
