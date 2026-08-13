namespace Humans.Tickets.Domain;

/// <summary>
/// Lifecycle states for a ticket transfer request.
/// Transitions: Pending → Approved | Rejected | Cancelled.
/// Approved/Rejected/Cancelled are terminal.
/// </summary>
internal enum TicketTransferStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}
