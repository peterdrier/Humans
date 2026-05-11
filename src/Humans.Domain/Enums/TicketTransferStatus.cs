namespace Humans.Domain.Enums;

/// <summary>
/// Lifecycle states for a ticket transfer request.
/// Transitions: Pending → Approved | Rejected | Cancelled.
/// Approved/Rejected/Cancelled are terminal.
/// </summary>
public enum TicketTransferStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}
