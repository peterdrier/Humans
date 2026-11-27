namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// Told when an order's tickets are issued and saved, so the buyer can be sent them, and of the other moments a buyer
/// or holder is mailed about. Registered implementations run in turn; one that throws is logged and never undoes the
/// change. The reconcile job tells <see cref="TicketsIssuedAsync"/> again, every run for up to seven days, until the
/// order's timeline has a <c>TicketsEmailed</c> or <c>TicketMailSkipped</c> row: an implementation must dedupe a repeat
/// and must record one of those two rows once it is done. The other calls come once per moment.
/// </summary>
internal interface ITicketDelivery
{
    Task TicketsIssuedAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>An order that reached its platform ended Failed or Expired.</summary>
    Task PaymentEndedAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>A ticket in resale sold; <paramref name="refundMinor"/> goes back to its holder in the evening batch.</summary>
    Task ResaleSoldAsync(Guid ticketId, long refundMinor, CancellationToken ct = default);

    /// <summary>Sales closed with the ticket still in the resale queue.</summary>
    Task ResaleUnsoldAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>The ticketing team renamed the ticket: its holder gets it again under the new name.</summary>
    Task TicketRenamedAsync(Guid ticketId, CancellationToken ct = default);
}
