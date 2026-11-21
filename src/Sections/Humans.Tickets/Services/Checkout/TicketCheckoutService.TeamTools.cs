using System.Diagnostics;
using System.Security.Cryptography;
using Humans.AuditLog.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>The ticketing team's queue work and the super admin's event cancellation. All audited.</summary>
internal sealed partial class TicketCheckoutService
{
    /// <summary>Kept under the reconcile job's four minute lock, leaving room for its other work.</summary>
    private static readonly TimeSpan CancelSweepBudget = TimeSpan.FromMinutes(2);

    public async Task<CheckoutMutationResult> WithdrawOfferForHolderAsync(
        Guid offerId, string reason, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        var offer = await repository.GetOfferAsync(offerId, ct);
        if (offer is null || offer.State is not (TicketResaleOfferState.Open or TicketResaleOfferState.Reserved))
            return new("That ticket is not waiting in the resale queue.");
        if (offer.State == TicketResaleOfferState.Reserved)
            return new("A buyer's checkout is paying for this ticket right now; try again once it settles.");

        offer.State = TicketResaleOfferState.Withdrawn;
        offer.WithdrawnAt = clock.GetCurrentInstant();
        offer.DirectedTokenHash = null;
        offer.DirectedEmail = null;
        await repository.UpdateOfferAsync(offer, ct);
        var ticket = await repository.GetTicketAsync(offer.TicketId, ct);
        if (ticket?.State == TicketCheckoutTicketState.Offered)
        {
            ticket.State = TicketCheckoutTicketState.Valid;
            await repository.UpdateTicketAsync(ticket, ct);
        }

        var why = reason.Trim();
        var barcode = ticket?.Barcode ?? offer.TicketId.ToString();
        if (ticket is not null)
            await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleWithdrawn,
                $"Ticket {barcode} taken out of the resale queue by the ticketing team: {why}", actorUserId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketResaleWithdrawnByTeam, AuditEntityType, ticket?.OrderId ?? offer.Id,
            $"Ticket {barcode} taken out of the resale queue for its holder: {why}",
            actorUserId, offer.HolderUserId, AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> CancelSalesEventAsync(
        Guid salesEventId, string reason, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        var salesEvent = await repository.GetSalesEventAsync(salesEventId, ct);
        if (salesEvent is null)
            return new("That sales event no longer exists.");
        if (salesEvent.CancelledAt is not null)
            return new("The event is already cancelled.");

        var now = clock.GetCurrentInstant();
        var before = salesEvent.Mode;
        salesEvent.Mode = TicketSalesMode.Closed;
        salesEvent.ResaleOpenedAt = null;
        salesEvent.CancelledAt = now;
        salesEvent.CancelledByUserId = actorUserId;
        salesEvent.CancelReason = reason.Trim();
        salesEvent.UpdatedAt = now;
        await repository.SaveSalesEventAsync(salesEvent, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketSalesEventCancelled, SalesEventAuditEntityType, salesEvent.Id,
            $"Event cancelled (sales were {before}): every ticket is voided and every paid order refunded what its tickets cost. " +
            $"Reason: {salesEvent.CancelReason}", actorUserId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CancellationProgress?> GetCancellationProgressAsync(Guid salesEventId, CancellationToken ct = default)
    {
        var salesEvent = await repository.GetSalesEventAsync(salesEventId, ct);
        return salesEvent is null ? null : (await LoadCancellationAsync(salesEvent, ct)).Progress;
    }

    /// <summary>
    /// Works the cancelled events' orders until the budget runs out; the next run carries on. Each order is safe to
    /// repeat: what is already void stays void, and the cancellation's own refund is asked for once.
    /// </summary>
    private async Task<int> SweepCancelledEventsAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var changed = 0;
        foreach (var salesEvent in (await repository.GetSalesEventsAsync(ct)).Where(e => e.CancelledAt is not null))
        {
            var state = await LoadCancellationAsync(salesEvent, ct);
            foreach (var order in state.ToDo)
            {
                if (watch.Elapsed > CancelSweepBudget)
                    return changed;
                try
                {
                    if (await CancelOrderAsync(salesEvent, order, state, ct))
                        changed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Cancelling checkout order {OrderId} of cancelled event {SalesEventId} failed; next run retries",
                        order.Id, salesEvent.Id);
                }
            }
        }
        return changed;
    }

    /// <summary>False while a buyer's checkout holds one of the order's returned tickets: it waits for that to settle.</summary>
    private async Task<bool> CancelOrderAsync(
        TicketSalesEvent salesEvent, TicketCheckoutOrder order, CancellationState state, CancellationToken ct)
    {
        var live = state.Tickets[order.Id].Where(IsLive).ToList();
        var offers = state.Offers.Where(o => live.Any(t => t.Id == o.TicketId)
            && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved).ToList();
        if (offers.Any(o => o.State == TicketResaleOfferState.Reserved))
            return false;

        var why = $"Event cancelled: {salesEvent.CancelReason}";
        var now = clock.GetCurrentInstant();
        foreach (var offer in offers)
        {
            offer.State = TicketResaleOfferState.Withdrawn;
            offer.WithdrawnAt = now;
            offer.DirectedTokenHash = null;
            await repository.UpdateOfferAsync(offer, ct);
        }
        foreach (var ticket in live)
        {
            ticket.State = TicketCheckoutTicketState.Void;
            ticket.VoidedAt = now;
            ticket.VoidReason = Truncate(why);
            await repository.UpdateTicketAsync(ticket, ct);
            if (ticket.HolderUserId is { } holder)
                holdings.InvalidateHoldings(holder);
        }

        var refundMinor = state.HasCancelRefund(order) ? 0 : state.Refundable(order);
        var refundNote = ", nothing to refund";
        if (refundMinor > 0)
        {
            var cancelRefundId = CancellationState.CancelRefundId(salesEvent.Id, order.Id);
            var (result, refundId) = await RefundCoreAsync(order.Id, refundMinor, Truncate(why), null, ct, cancelRefundId);
            refundNote = $", {FormatMinor(refundMinor)} {order.Currency} refund asked for";
            if (!result.Succeeded)
            {
                logger.LogError("Cancellation refund of checkout order {OrderId} failed: {Refusal}", order.Id, result.Refusal);
                // Written as failed so the order leaves the to-do list and the team sees what is owed.
                if (refundId is null)
                    await repository.AddRefundAsync(new TicketCheckoutRefund
                    {
                        Id = cancelRefundId,
                        OrderId = order.Id,
                        AmountMinor = refundMinor,
                        Reason = Truncate(why),
                        State = RefundState.Failed,
                        FailureReason = Truncate(result.Refusal ?? "Refused"),
                        CreatedAt = now,
                    }, ct);
                refundNote = $"; the {FormatMinor(refundMinor)} {order.Currency} refund failed " +
                    $"({Truncate(result.Refusal ?? "refused")}): the ticketing team must refund it by hand";
                await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, AuditEntityType, order.Id,
                    $"Cancellation refund{refundNote[1..]}", JobName);
            }
        }

        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.EventCancelled,
            $"{why}. {live.Count} ticket(s) voided" + (offers.Count > 0 ? $", {offers.Count} taken out of the resale queue" : string.Empty) +
            refundNote, null, ct);
        var eventName = (await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct))?.EventName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(order.BuyerEmail))
            await email.SendAsync(emails.EventCancelled(order.BuyerUserId, order.BuyerEmail, order.BuyerName, eventName, refundMinor,
                order.Lines.Any(l => l.Kind == CheckoutLineKind.Donation), order.Culture), ct);
        return true;
    }

    private static bool IsLive(TicketCheckoutTicket t) =>
        t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered;

    private async Task<CancellationState> LoadCancellationAsync(TicketSalesEvent salesEvent, CancellationToken ct) =>
        new(salesEvent,
            await repository.GetOrdersForSalesEventAsync(salesEvent.Id, ct),
            (await repository.GetTicketsForSalesEventAsync(salesEvent.Id, ct)).ToLookup(t => t.OrderId),
            (await repository.GetRefundsForSalesEventAsync(salesEvent.Id, ct)).ToLookup(r => r.OrderId),
            await repository.GetOffersForSalesEventAsync(salesEvent.Id, ct),
            clock.GetCurrentInstant());

    /// <summary>
    /// One cancelled event as it stands. Before it is cancelled, <see cref="Since"/> is now, so the progress reads as
    /// what a cancellation would do.
    /// </summary>
    private sealed class CancellationState(
        TicketSalesEvent salesEvent,
        IReadOnlyList<TicketCheckoutOrder> orders,
        ILookup<Guid, TicketCheckoutTicket> tickets,
        ILookup<Guid, TicketCheckoutRefund> refunds,
        IReadOnlyList<TicketResaleOffer> offers,
        Instant now)
    {
        public ILookup<Guid, TicketCheckoutTicket> Tickets => tickets;

        public IReadOnlyList<TicketResaleOffer> Offers => offers;

        private Instant Since => salesEvent.CancelledAt ?? now;

        private List<TicketCheckoutOrder> Paid => orders.Where(o => o.State == TicketCheckoutOrderState.Paid).ToList();

        public long Refundable(TicketCheckoutOrder order) =>
            new CheckoutOrderDetail(order, refunds[order.Id].ToList(), [], tickets[order.Id].ToList()).RefundableMinor;

        /// <summary>
        /// The cancellation's own refund of an order has a fixed id, so a refund the team makes meanwhile never stands in for
        /// it, and a repeat finds the one already asked for (a declined one is not tried again).
        /// </summary>
        public static Guid CancelRefundId(Guid salesEventId, Guid orderId)
        {
            Span<byte> both = stackalloc byte[32];
            salesEventId.TryWriteBytes(both[..16]);
            orderId.TryWriteBytes(both[16..]);
            return new Guid(SHA256.HashData(both)[..16]);
        }

        public bool HasCancelRefund(TicketCheckoutOrder order) =>
            refunds[order.Id].Any(r => r.Id == CancelRefundId(salesEvent.Id, order.Id));

        /// <summary>Paid orders with a ticket still working, or money still owed that the cancellation has not asked for.</summary>
        public IReadOnlyList<TicketCheckoutOrder> ToDo => Paid
            .Where(o => tickets[o.Id].Any(IsLive) || (Refundable(o) > 0 && !HasCancelRefund(o)))
            .OrderBy(o => o.PaidAt)
            .ToList();

        public CancellationProgress Progress
        {
            get
            {
                var paid = Paid;
                var since = paid.SelectMany(o => refunds[o.Id]).Where(r => r.CreatedAt >= Since).ToList();
                return new CancellationProgress(
                    salesEvent.CancelledAt, salesEvent.CancelledByUserId, salesEvent.CancelReason,
                    PaidOrders: paid.Count,
                    ToDo: ToDo.Count,
                    LiveTickets: paid.Sum(o => tickets[o.Id].Count(IsLive)),
                    TicketMoneyMinor: paid.Sum(o => o.TicketTotalMinor),
                    DisputedMinor: paid.Sum(o => o.DisputedMinor),
                    RefundedMinor: paid.Sum(o => Math.Max(o.RefundedMinor,
                        refunds[o.Id].Where(r => r.State == RefundState.Succeeded).Sum(r => r.AmountMinor))),
                    InFlightMinor: paid.Sum(o => refunds[o.Id].Where(r => r.State is RefundState.Pending or RefundState.ManualRequired).Sum(r => r.AmountMinor)),
                    StillRefundableMinor: paid.Sum(Refundable),
                    DonationsMinor: paid.Sum(o => o.TotalMinor - o.TicketTotalMinor),
                    RefundsSince: since.GroupBy(r => r.State).OrderBy(g => g.Key)
                        .Select(g => new RefundStateRow(g.Key, g.Count(), g.Sum(r => r.AmountMinor))).ToList());
            }
        }
    }
}

/// <summary>
/// A cancellation's checks and balances, over paid orders. Ticket money = chargebacks + refunded + refunds in flight +
/// still refundable, unless a refund made outside Humans went past the ticket lines. <see cref="RefundsSince"/> counts
/// the refunds asked for since the cancellation, by state. <see cref="ToDo"/> is what the reconcile job has not reached.
/// </summary>
internal sealed record CancellationProgress(
    Instant? CancelledAt,
    Guid? CancelledByUserId,
    string? Reason,
    int PaidOrders,
    int ToDo,
    int LiveTickets,
    long TicketMoneyMinor,
    long DisputedMinor,
    long RefundedMinor,
    long InFlightMinor,
    long StillRefundableMinor,
    long DonationsMinor,
    IReadOnlyList<RefundStateRow> RefundsSince);
