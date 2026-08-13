using Humans.Tickets.Contracts;
using Humans.Tickets.Services.Dtos;
using NodaTime;

namespace Humans.Tickets.Models;

/// <summary>
/// Transfer-wizard half of the ticket-stub projection. The holdings half lives on
/// <see cref="TicketStubInfo.From(UserTicketHoldingRow, LocalDate?)"/>, which Shell's
/// stub widgets call; this one cannot join it there because
/// <see cref="MyAttendeeRowDto"/> is internal to the section and a public method cannot
/// take an internal parameter type (CS0051).
/// </summary>
internal static class TicketStubMapping
{
    /// <inheritdoc cref="TicketStubInfo.From(UserTicketHoldingRow, LocalDate?)"/>
    public static TicketStubInfo ToStub(this MyAttendeeRowDto row, LocalDate? holderEarlyEntry) =>
        new(row.AttendeeName, row.AttendeeEmail, row.Status,
            row.HasPendingOutgoingTransfer, row.PendingTransferRequestId, holderEarlyEntry);
}
