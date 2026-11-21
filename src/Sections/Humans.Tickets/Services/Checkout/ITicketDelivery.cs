namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// Told once an order's tickets are issued and saved, so the buyer can be sent them. Registered implementations run
/// in turn; one that throws is logged and never undoes the sale.
/// </summary>
internal interface ITicketDelivery
{
    Task TicketsIssuedAsync(Guid orderId, CancellationToken ct = default);
}
