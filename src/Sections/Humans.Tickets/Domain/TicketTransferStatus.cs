namespace Humans.Tickets.Domain;

/// <summary>Pending, then one of the three terminal states.</summary>
internal enum TicketTransferStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}
