using System.Security.Cryptography;
using System.Text;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Base.Extensions;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>Issued tickets and the resale queue: return, withdraw, directed resale, and the refund when one sells.</summary>
internal sealed partial class TicketCheckoutService
{
    public async Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForOrderAsync(Guid orderId, CancellationToken ct = default) =>
        (await repository.GetTicketsForOrderAsync(orderId, ct)).OrderBy(t => t.Kind).ThenBy(t => t.IssuedAt).ToList();

    public async Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default) =>
        (await repository.GetTicketsForHolderAsync(userId, ct)).OrderByDescending(t => t.IssuedAt).ToList();

    public async Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
        (await repository.GetTicketsForSalesEventAsync(salesEventId, ct)).OrderBy(t => t.IssuedAt).ToList();

    public async Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
        (await repository.GetOffersForSalesEventAsync(salesEventId, ct)).OrderBy(o => o.OfferedAt).ToList();

    public async Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSalesEventAsync(Guid salesEventId, CancellationToken ct = default) =>
        (await repository.GetRefundsForSalesEventAsync(salesEventId, ct)).OrderBy(r => r.CreatedAt).ToList();

    public async Task<IReadOnlyList<HeldTicket>> GetHeldTicketsAsync(Guid userId, CancellationToken ct = default)
    {
        var held = new List<HeldTicket>();
        var now = clock.GetCurrentInstant();
        foreach (var group in (await repository.GetTicketsForHolderAsync(userId, ct))
                     .Where(t => t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered)
                     .GroupBy(t => t.SalesEventId))
        {
            var salesEvent = await repository.GetSalesEventAsync(group.Key, ct);
            var eventName = salesEvent is null ? string.Empty
                : (await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct))?.EventName ?? string.Empty;
            var offers = await repository.GetOffersForSalesEventAsync(group.Key, ct);
            var queue = offers.Where(o => o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved)
                .OrderBy(o => o.OfferedAt).ToList();
            var eventTickets = salesEvent is null ? [] : await repository.GetTicketsForSalesEventAsync(group.Key, ct);
            foreach (var ticket in group.OrderBy(t => t.IssuedAt))
            {
                var offer = queue.FirstOrDefault(o => o.TicketId == ticket.Id);
                var hasDependents = eventTickets.Any(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid);
                held.Add(new HeldTicket(ticket, eventName, offer, offer is null ? null : queue.IndexOf(offer) + 1,
                    CanOffer: offer is null && ticket.Kind == TicketHolderKind.Adult && salesEvent is { Mode: not TicketSalesMode.Closed },
                    HasDependents: hasDependents,
                    DirectedActive: offer?.IsDirectedTo(now) == true));
            }
        }
        return held;
    }

    public async Task<CheckoutMutationResult> OfferForResaleAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null || ticket.HolderUserId != userId)
            return new("Tickets_Resale_NotFound");
        if (ticket.State != TicketCheckoutTicketState.Valid || ticket.Kind != TicketHolderKind.Adult)
            return new("Tickets_Resale_NotOfferable");
        var salesEvent = await repository.GetSalesEventAsync(ticket.SalesEventId, ct);
        if (salesEvent is null || salesEvent.Mode == TicketSalesMode.Closed)
            return new("Tickets_Resale_Closed");

        // Children go wherever their adult goes: returned with the adult and refunded with it when it sells.
        var dependents = (await repository.GetTicketsForOrderAsync(ticket.OrderId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .ToList();
        var now = clock.GetCurrentInstant();
        var offer = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            SalesEventId = ticket.SalesEventId,
            TicketId = ticket.Id,
            HolderUserId = userId,
            OfferedAt = now,
            State = TicketResaleOfferState.Open,
            RefundOwedMinor = ticket.PricePaidMinor + dependents.Sum(d => d.PricePaidMinor),
        };
        await repository.AddOfferAsync(offer, ct);
        ticket.State = TicketCheckoutTicketState.Offered;
        await repository.UpdateTicketAsync(ticket, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleOffered,
            $"Ticket {ticket.Barcode} returned to the resale queue; {FormatMinor(offer.RefundOwedMinor)} owed if it sells" +
            (dependents.Count > 0 ? $", with {dependents.Count} child ticket(s)" : string.Empty), userId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketResaleOffered, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} offered for resale", userId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> WithdrawOfferAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var (ticket, offer) = await OwnOfferAsync(ticketId, userId, ct);
        if (ticket is null || offer is null)
            return new("Tickets_Resale_NotFound");
        if (offer.State == TicketResaleOfferState.Reserved)
            return new("Tickets_Resale_InCheckout");

        offer.State = TicketResaleOfferState.Withdrawn;
        offer.WithdrawnAt = clock.GetCurrentInstant();
        offer.DirectedTokenHash = null;
        await repository.UpdateOfferAsync(offer, ct);
        ticket.State = TicketCheckoutTicketState.Valid;
        await repository.UpdateTicketAsync(ticket, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleWithdrawn,
            $"Ticket {ticket.Barcode} taken back out of the resale queue", userId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketResaleWithdrawn, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} withdrawn from resale", userId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> InviteBuyerAsync(
        Guid ticketId, Guid userId, string inviteeEmail, Uri claimUrlBase, string culture, CancellationToken ct = default)
    {
        var (ticket, offer) = await OwnOfferAsync(ticketId, userId, ct);
        if (ticket is null || offer is null)
            return new("Tickets_Resale_NotFound");
        if (offer.State != TicketResaleOfferState.Open)
            return new("Tickets_Resale_InCheckout");
        inviteeEmail = inviteeEmail.Trim();
        if (!LooksLikeEmail(inviteeEmail) || string.Equals(inviteeEmail, ticket.HolderEmail, StringComparison.OrdinalIgnoreCase))
            return new("Tickets_Resale_InviteEmailInvalid");
        var salesEvent = (await repository.GetSalesEventAsync(ticket.SalesEventId, ct))!;
        var now = clock.GetCurrentInstant();
        if (!(await LoadStockAsync(salesEvent, now, ct)).ResaleOpen)
            return new("Tickets_Resale_NotOpenYet");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var days = Math.Max(1, salesEvent.DirectedResaleDays);
        offer.DirectedEmail = inviteeEmail;
        offer.DirectedTokenHash = HashToken(token);
        offer.DirectedExpiresAt = now + Duration.FromDays(days);
        offer.DirectedUsedAt = null;
        await repository.UpdateOfferAsync(offer, ct);

        var eventName = (await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct))?.EventName ?? string.Empty;
        await email.SendAsync(emails.ResaleInvitation(
            inviteeEmail, ticket.HolderName, eventName, new Uri(claimUrlBase, token).ToString(), days, culture), ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInvited,
            $"Holder invited one buyer for ticket {ticket.Barcode}; the link lapses {offer.DirectedExpiresAt.Value.ToIso8601()}",
            userId, ct);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> CancelInviteAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var (ticket, offer) = await OwnOfferAsync(ticketId, userId, ct);
        if (ticket is null || offer?.DirectedTokenHash is null)
            return new("Tickets_Resale_NotFound");
        if (offer.State == TicketResaleOfferState.Reserved)
            return new("Tickets_Resale_InCheckout");
        offer.DirectedTokenHash = null;
        offer.DirectedExpiresAt = null;
        await repository.UpdateOfferAsync(offer, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInviteCancelled,
            $"Invitation for ticket {ticket.Barcode} cancelled; it keeps its place in the queue", userId, ct);
        return CheckoutMutationResult.Ok;
    }

    private async Task<(TicketCheckoutTicket? Ticket, TicketResaleOffer? Offer)> OwnOfferAsync(
        Guid ticketId, Guid userId, CancellationToken ct)
    {
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null || ticket.HolderUserId != userId || ticket.State != TicketCheckoutTicketState.Offered)
            return (null, null);
        var offer = (await repository.GetOffersForSalesEventAsync(ticket.SalesEventId, ct))
            .FirstOrDefault(o => o.TicketId == ticket.Id && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved);
        return (ticket, offer);
    }

    /// <summary>The invited offer when the link is live, bound to the signed-in buyer's email and the queue still open.</summary>
    private static TicketResaleOffer? DirectedOfferFor(TicketResaleOffer offer, BuyContext context) =>
        offer.State == TicketResaleOfferState.Open && offer.IsDirectedTo(context.Now)
        && string.Equals(offer.DirectedEmail, context.Buyer?.Email, StringComparison.OrdinalIgnoreCase)
        && offer.SalesEventId == context.SalesEvent.Id
            ? offer
            : null;

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task ReserveOfferAsync(TicketResaleOffer offer, Guid orderId, CancellationToken ct)
    {
        offer.State = TicketResaleOfferState.Reserved;
        offer.ReservedByOrderId = orderId;
        await repository.UpdateOfferAsync(offer, ct);
    }

    /// <summary>The order will never pay: its reserved offers go back to the queue, keeping their place.</summary>
    private async Task ReleaseOffersAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        foreach (var offerId in order.Lines.Select(l => l.ResaleOfferId).OfType<Guid>())
        {
            var offer = await repository.GetOfferAsync(offerId, ct);
            if (offer is not { State: TicketResaleOfferState.Reserved } || offer.ReservedByOrderId != order.Id)
                continue;
            offer.State = TicketResaleOfferState.Open;
            offer.ReservedByOrderId = null;
            await repository.UpdateOfferAsync(offer, ct);
        }
    }

    /// <summary>
    /// Issues the paid order's tickets (once), completes any resale it bought, and tells delivery. Safe to repeat: a
    /// second call only finishes resales a crash left open.
    /// </summary>
    private async Task CompletePaidAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        if ((await repository.GetTicketsForOrderAsync(order.Id, ct)).Count > 0)
        {
            foreach (var resold in order.Lines.Select(l => l.ResaleOfferId).OfType<Guid>())
                await CompleteResaleAsync(resold, order, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var ids = order.Lines.Select(_ => Guid.NewGuid()).ToList();
        var tickets = order.Lines
            .Select((line, index) => (line, index))
            .Where(x => x.line.Kind == CheckoutLineKind.Ticket)
            .Select(x => new TicketCheckoutTicket
            {
                Id = ids[x.index],
                SalesEventId = order.SalesEventId,
                OrderId = order.Id,
                Kind = x.line.HolderKind ?? TicketHolderKind.Adult,
                ParentTicketId = x.line.ParentLineIndex is { } parent ? ids[parent] : null,
                HolderUserId = x.line.AttendeeUserId,
                HolderName = x.line.AttendeeName ?? string.Empty,
                HolderEmail = x.line.AttendeeEmail,
                Barcode = NewBarcode(),
                PricePaidMinor = x.line.AmountMinor,
                WaveName = x.line.WaveName ?? string.Empty,
                ClassProgramId = x.line.ClassProgramId,
                ClassLabel = x.line.ClassLabel,
                CreditsMinor = x.line.CreditsMinor,
                State = TicketCheckoutTicketState.Valid,
                IssuedAt = now,
            })
            .ToList();
        if (tickets.Count == 0)
            return;
        await repository.AddTicketsAsync(tickets, ct);
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.TicketsIssued,
            $"{tickets.Count} ticket(s) issued: {string.Join(", ", tickets.Select(t => t.Barcode))}", null, ct);

        foreach (var offerId in order.Lines.Select(l => l.ResaleOfferId).OfType<Guid>())
            await CompleteResaleAsync(offerId, order, ct);

        await DeliverAsync(order.Id, ct);
    }

    private async Task DeliverAsync(Guid orderId, CancellationToken ct)
    {
        foreach (var delivery in deliveries)
        {
            try
            {
                await delivery.TicketsIssuedAsync(orderId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ticket delivery for order {OrderId} failed", orderId);
            }
        }
    }

    private async Task CompleteResaleAsync(Guid offerId, TicketCheckoutOrder buyerOrder, CancellationToken ct)
    {
        var offer = await repository.GetOfferAsync(offerId, ct);
        if (offer is null || offer.State == TicketResaleOfferState.Sold)
            return;
        var now = clock.GetCurrentInstant();
        var directed = offer.DirectedTokenHash is not null;
        offer.State = TicketResaleOfferState.Sold;
        offer.SoldAt = now;
        offer.SoldToOrderId = buyerOrder.Id;
        offer.ReservedByOrderId = null;
        if (directed)
            offer.DirectedUsedAt = now;
        await repository.UpdateOfferAsync(offer, ct);

        var old = await repository.GetTicketAsync(offer.TicketId, ct);
        if (old is null)
            return;
        old.State = TicketCheckoutTicketState.Resold;
        await repository.UpdateTicketAsync(old, ct);
        foreach (var child in (await repository.GetTicketsForOrderAsync(old.OrderId, ct))
                     .Where(t => t.ParentTicketId == old.Id && t.State == TicketCheckoutTicketState.Valid))
        {
            child.State = TicketCheckoutTicketState.Void;
            child.VoidedAt = now;
            child.VoidReason = "Returned with the adult's ticket";
            await repository.UpdateTicketAsync(child, ct);
        }
        await TimelineAsync(old.OrderId, TicketCheckoutOrderEventKind.ResaleSold,
            $"Ticket {old.Barcode} resold" + (directed ? " to the invited buyer" : string.Empty) +
            $"; refunding {FormatMinor(offer.RefundOwedMinor)}", null, ct);
        if (directed)
            await TimelineAsync(buyerOrder.Id, TicketCheckoutOrderEventKind.ResaleInviteUsed,
                "Bought through a resale invitation", buyerOrder.BuyerUserId, ct);

        var (result, refundId) = await RefundCoreAsync(old.OrderId, offer.RefundOwedMinor, "Ticket resold", null, ct);
        offer.RefundId = refundId;
        await repository.UpdateOfferAsync(offer, ct);
        if (!result.Succeeded)
            logger.LogError("Resale refund for ticket {TicketId} failed: {Refusal}", old.Id, result.Refusal);
    }

    /// <summary>
    /// Lapses expired invitations (the offer keeps its place) and repairs offers held by a checkout that will never
    /// pay or already paid. Returns how many changed.
    /// </summary>
    private async Task<int> SweepOffersAsync(CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var changed = 0;
        foreach (var salesEvent in await repository.GetSalesEventsAsync(ct))
        {
            foreach (var offer in await repository.GetOffersForSalesEventAsync(salesEvent.Id, ct))
            {
                if (offer.State == TicketResaleOfferState.Open && offer.DirectedTokenHash is not null
                    && offer.DirectedUsedAt is null && offer.DirectedExpiresAt <= now)
                {
                    offer.DirectedTokenHash = null;
                    await repository.UpdateOfferAsync(offer, ct);
                    if (await repository.GetTicketAsync(offer.TicketId, ct) is { } ticket)
                        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInviteLapsed,
                            $"Invitation for ticket {ticket.Barcode} lapsed unused; it stays in the queue", null, ct);
                    changed++;
                }
                else if (offer is { State: TicketResaleOfferState.Reserved, ReservedByOrderId: { } orderId }
                         && await repository.GetOrderAsync(orderId, ct) is { } holder
                         && holder.State is not (TicketCheckoutOrderState.Created or TicketCheckoutOrderState.Pending))
                {
                    if (holder.State == TicketCheckoutOrderState.Paid)
                        await CompletePaidAsync(holder, ct);
                    else
                        await ReleaseOffersAsync(holder, ct);
                    changed++;
                }
            }
        }
        return changed;
    }
}
