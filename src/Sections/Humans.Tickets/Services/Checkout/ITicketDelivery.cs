namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// Told when an order's tickets are issued and saved, so the buyer can be sent them. Registered implementations run
/// in turn; one that throws is logged and never undoes the sale. The reconcile job tells it again, every run for up to
/// seven days, until the order's timeline has a <c>TicketsEmailed</c> or <c>TicketMailSkipped</c> row: an
/// implementation must dedupe a repeat and must record one of those two rows once it is done.
/// </summary>
internal interface ITicketDelivery
{
    Task TicketsIssuedAsync(Guid orderId, CancellationToken ct = default);
}
