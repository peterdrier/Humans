using Humans.Base.Interfaces;
using Humans.Tickets.Services.Checkout;

namespace Humans.Tickets.Services.Delivery;

/// <summary>The mail an order's buyer and ticket holders get. Every send is visible in the Email outbox.</summary>
internal interface ITicketDelivery : IApplicationService
{
    /// <summary>
    /// Called by checkout once a paid order's tickets are saved: a ticket email per holder, the receipt to the payer,
    /// and the volunteer invitation scheduled for later. Safe to repeat while the outbox still holds the first send.
    /// </summary>
    Task TicketsIssuedAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>The ticketing team's resend: every ticket email and the receipt go out again.</summary>
    Task<CheckoutMutationResult> ResendAsync(Guid orderId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>The scheduled follow-up; skipped when the ticket is no longer valid.</summary>
    Task SendJoinInvitationAsync(Guid ticketId, CancellationToken ct = default);
}
