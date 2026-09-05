using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Render model for the <c>&lt;vc:ticket-stub&gt;</c> component — one held ticket
/// drawn as a physical admission stub. Shared by the transfer wizard,
/// <c>/Profile/Me</c>, the homepage and the Scanner section's ticket card. A pending
/// outgoing transfer renders a "transfer pending" stamp on the stub.
/// </summary>
/// <remarks>
/// The component itself is <c>Humans.Tickets/Contracts/TicketStubViewComponent</c>;
/// this model is on the leaf because the section's <c>MyTicketStubs</c> /
/// <c>TicketHoldings</c> view components and Scanner's ticket card project into it from
/// <see cref="UserTicketHoldingRow"/>. The transfer wizard's own
/// <c>MyAttendeeRowDto</c> projection is an internal mapper in the section — a
/// public method cannot take an internal parameter type (CS0051).
/// </remarks>
public sealed record TicketStubInfo(
    string AttendeeName,
    string? AttendeeEmail,
    TicketAttendeeStatus Status,
    bool HasPendingTransfer,
    Guid? PendingTransferRequestId,
    LocalDate? EarlyEntryDate)
{
    /// <summary>
    /// Projects a holdings row into a stub, stamping the holder's earliest entry
    /// date. Single mapper for every surface that renders a holder's own stubs
    /// (homepage strip + holdings list + transfer wizard) so the EE pill can never
    /// be dropped on one surface and present on another. <paramref name="holderEarlyEntry"/>
    /// is the viewer's EE (one value, shown on each of their stubs); null = no EE.
    /// </summary>
    public static TicketStubInfo From(UserTicketHoldingRow row, LocalDate? holderEarlyEntry) =>
        new(row.AttendeeName, row.AttendeeEmail, row.Status,
            row.HasPendingOutgoingTransfer, row.PendingTransferRequestId, holderEarlyEntry);
}
