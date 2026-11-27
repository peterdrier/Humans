using System.Diagnostics;
using System.Security.Cryptography;
using Humans.AuditLog.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
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
            return new("A buyer's order is paying for this ticket right now; try again once it settles.");

        offer.State = TicketResaleOfferState.Withdrawn;
        offer.WithdrawnAt = clock.GetCurrentInstant();
        offer.Invites = [];
        var ticket = await repository.GetTicketAsync(offer.TicketId, ct);
        if (ticket?.State == TicketCheckoutTicketState.Offered)
        {
            ticket.State = TicketCheckoutTicketState.Valid;
            await repository.UpdateTicketAsync(ticket, ct, offer: offer);
        }
        else
            await repository.UpdateOfferAsync(offer, ct);

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

    public async Task<CheckoutMutationResult> CancelEventAsync(
        Guid settingsId, string reason, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        var ticketing = await repository.GetTicketingSettingsAsync(settingsId, ct);
        if (ticketing is null)
            return new("Those ticketing settings no longer exist.");
        if (ticketing.CancelledAt is not null)
            return new("The event is already cancelled.");

        var now = clock.GetCurrentInstant();
        var before = ticketing.Mode;
        ticketing.Mode = TicketSalesMode.Closed;
        ticketing.ResaleOpenedAt = null;
        ticketing.CancelledAt = now;
        ticketing.CancelledByUserId = actorUserId;
        ticketing.CancelReason = reason.Trim();
        ticketing.UpdatedAt = now;
        await repository.SaveTicketingSettingsAsync(ticketing, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketSalesEventCancelled, TicketingSettingsAuditEntityType, ticketing.Id,
            $"Event cancelled (sales were {before}): every ticket is voided and every paid order refunded what its tickets cost. " +
            $"Reason: {ticketing.CancelReason}", actorUserId);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CancellationProgress?> GetCancellationProgressAsync(Guid settingsId, CancellationToken ct = default)
    {
        var ticketing = await repository.GetTicketingSettingsAsync(settingsId, ct);
        return ticketing is null ? null : (await LoadCancellationAsync(ticketing, ct)).Progress;
    }

    /// <summary>
    /// Works the cancelled events' orders until the budget runs out; the next run carries on. Each order is safe to
    /// repeat: what is already void stays void, and the cancellation's own refund is asked for once.
    /// </summary>
    private async Task<int> SweepCancelledEventsAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var changed = 0;
        foreach (var ticketing in (await repository.ListTicketingSettingsAsync(ct)).Where(e => e.CancelledAt is not null))
        {
            var state = await LoadCancellationAsync(ticketing, ct);
            foreach (var order in state.ToDo)
            {
                if (watch.Elapsed > CancelSweepBudget)
                    return changed;
                try
                {
                    if (await CancelOrderAsync(ticketing, order, state, ct))
                        changed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Cancelling order {OrderId} of cancelled event {TicketingSettingsId} failed; next run retries",
                        order.Id, ticketing.Id);
                }
            }
        }
        return changed;
    }

    /// <summary>False while a buyer's unpaid order holds one of the order's tickets in resale: it waits for that to settle.</summary>
    private async Task<bool> CancelOrderAsync(
        TicketingSettings ticketing, TicketCheckoutOrder order, CancellationState state, CancellationToken ct)
    {
        var live = state.Tickets[order.Id].Where(t => t.IsHeld).ToList();
        var offers = state.Offers.Where(o => live.Any(t => t.Id == o.TicketId)
            && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved).ToList();
        if (offers.Any(o => o.State == TicketResaleOfferState.Reserved))
            return false;

        var why = $"Event cancelled: {ticketing.CancelReason}";
        var now = clock.GetCurrentInstant();
        foreach (var offer in offers)
        {
            offer.State = TicketResaleOfferState.Withdrawn;
            offer.WithdrawnAt = now;
            offer.Invites = [];
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

        // A repeat after a stop part way finds the refund already asked for, so the email still names it.
        var (refundMinor, refundByHand) = state.CancelRefund(order) is { } asked
            ? (asked.AmountMinor, asked.State is RefundState.Failed or RefundState.ManualRequired)
            : (state.Refundable(order), false);
        string? refundNote = null;
        if (refundMinor > 0 && !state.HasCancelRefund(order))
        {
            var cancelRefundId = CancellationState.CancelRefundId(ticketing.Id, order.Id);
            var (result, refundId) = await RefundCoreAsync(order.Id, refundMinor, Truncate(why), null, ct, cancelRefundId);
            if (!result.Succeeded)
            {
                refundByHand = true;
                logger.LogError("Cancellation refund of order {OrderId} failed: {Refusal}", order.Id, result.Refusal);
                // Written as failed so it is not asked again and the team sees what is owed.
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
            else if (refundId is { } id)
                refundByHand = (await repository.GetRefundsAsync(order.Id, ct))
                    .Any(r => r.Id == id && r.State is RefundState.Failed or RefundState.ManualRequired);
        }
        refundNote ??= refundMinor <= 0 ? ", nothing to refund"
            : refundByHand ? $"; the ticketing team must refund {FormatMinor(refundMinor)} {order.Currency} by hand"
            : $", {FormatMinor(refundMinor)} {order.Currency} refund asked for";

        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.EventCancelled,
            $"{why}. {live.Count} ticket(s) voided" + (offers.Count > 0 ? $", {offers.Count} taken out of the resale queue" : string.Empty) +
            refundNote, null, ct);
        var buyer = await users.GetUserInfoAsync(order.BuyerUserId, ct);
        var address = buyer?.Email ?? order.BuyerEmail;
        if (buyer is not { IsTombstone: false } || TicketPiiTombstone.IsTombstoned(order.BuyerName, order.BuyerEmail)
            || string.IsNullOrWhiteSpace(address))
        {
            if (!state.HasSkippedNotice(order))
                await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.CancellationNoticeSkipped,
                    "Cancellation notice not sent: buyer account or contact is unavailable", null, ct);
            return true;
        }
        var eventName = (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct))?.EventName ?? string.Empty;
        var notice = emails.EventCancelled(buyer.Id, address, order.BuyerName, eventName, refundMinor,
            refundByHand, order.Lines.Any(l => l.Kind == OrderLineKind.Donation), order.Culture);
        await email.SendAsync(notice with { DeliveryId = CancellationState.CancelNoticeId(ticketing.Id, order.Id) }, ct);
        await repository.MarkCancellationEmailedAsync(order.Id, clock.GetCurrentInstant(), ct);
        return true;
    }

    private async Task<CancellationState> LoadCancellationAsync(TicketingSettings ticketing, CancellationToken ct)
    {
        var orders = await repository.GetOrdersForSettingsAsync(ticketing.Id, ct);
        var noticeOrders = orders.Where(o => o.State == TicketCheckoutOrderState.Paid && o.CancellationEmailedAt is null)
            .Select(o => o.Id).ToList();
        var skipped = (await repository.GetOrderEventsAsync(noticeOrders, ct))
            .Where(e => e.Kind == TicketCheckoutOrderEventKind.CancellationNoticeSkipped).Select(e => e.OrderId).ToHashSet();
        return new CancellationState(ticketing, orders,
            (await repository.GetTicketsForSettingsAsync(ticketing.Id, ct)).ToLookup(t => t.OrderId),
            (await repository.GetRefundsForSettingsAsync(ticketing.Id, ct)).ToLookup(r => r.OrderId),
            await repository.GetOffersForSettingsAsync(ticketing.Id, ct), skipped,
            clock.GetCurrentInstant());
    }

    /// <summary>
    /// One cancelled event as it stands. Before it is cancelled, <see cref="Since"/> is now, so the progress reads as
    /// what a cancellation would do.
    /// </summary>
    private sealed class CancellationState(
        TicketingSettings ticketing,
        IReadOnlyList<TicketCheckoutOrder> orders,
        ILookup<Guid, TicketCheckoutTicket> tickets,
        ILookup<Guid, TicketCheckoutRefund> refunds,
        IReadOnlyList<TicketResaleOffer> offers,
        IReadOnlySet<Guid> skippedNotices,
        Instant now)
    {
        public ILookup<Guid, TicketCheckoutTicket> Tickets => tickets;

        public IReadOnlyList<TicketResaleOffer> Offers => offers;

        private Instant Since => ticketing.CancelledAt ?? now;

        private List<TicketCheckoutOrder> Paid => orders.Where(o => o.State == TicketCheckoutOrderState.Paid).ToList();

        public long Refundable(TicketCheckoutOrder order) =>
            new OrderDetail(order, refunds[order.Id].ToList(), [], tickets[order.Id].ToList()).RefundableMinor;

        /// <summary>
        /// The cancellation's own refund of an order has a fixed id, so a refund the team makes meanwhile never stands in for
        /// it, and a repeat finds the one already asked for (a declined one is not tried again).
        /// </summary>
        public static Guid CancelRefundId(Guid settingsId, Guid orderId)
        {
            Span<byte> both = stackalloc byte[32];
            settingsId.TryWriteBytes(both[..16]);
            orderId.TryWriteBytes(both[16..]);
            return new Guid(SHA256.HashData(both)[..16]);
        }

        /// <summary>The cancellation email's delivery id: a repeat after a stop before the order was marked emailed reuses it.</summary>
        public static Guid CancelNoticeId(Guid settingsId, Guid orderId) =>
            new(SHA256.HashData(CancelRefundId(settingsId, orderId).ToByteArray())[..16]);

        public TicketCheckoutRefund? CancelRefund(TicketCheckoutOrder order) =>
            refunds[order.Id].FirstOrDefault(r => r.Id == CancelRefundId(ticketing.Id, order.Id));

        public bool HasCancelRefund(TicketCheckoutOrder order) => CancelRefund(order) is not null;

        public bool HasSkippedNotice(TicketCheckoutOrder order) => skippedNotices.Contains(order.Id);

        /// <summary>
        /// Paid orders awaiting notice processing (email or explicit skip), with a ticket still working, or with money still owed
        /// that the cancellation has not asked for.
        /// </summary>
        public IReadOnlyList<TicketCheckoutOrder> ToDo => Paid
            .Where(o => (o.CancellationEmailedAt is null && !HasSkippedNotice(o))
                || tickets[o.Id].Any(t => t.IsHeld) || (Refundable(o) > 0 && !HasCancelRefund(o)))
            .OrderBy(o => o.PaidAt)
            .ToList();

        public CancellationProgress Progress
        {
            get
            {
                var paid = Paid;
                var since = paid.SelectMany(o => refunds[o.Id]).Where(r => r.CreatedAt >= Since).ToList();
                return new CancellationProgress(
                    ticketing.CancelledAt, ticketing.CancelledByUserId, ticketing.CancelReason,
                    PaidOrders: paid.Count,
                    ToDo: ToDo.Count,
                    LiveTickets: paid.Sum(o => tickets[o.Id].Count(t => t.IsHeld)),
                    TicketMoneyMinor: paid.Sum(o => o.TicketTotalMinor),
                    ChargedBackMinor: paid.Sum(o => o.ChargedBackMinor),
                    RefundedMinor: paid.Sum(o => Math.Max(o.RefundedMinor,
                        refunds[o.Id].Where(r => r.State == RefundState.Succeeded).Sum(r => r.AmountMinor))),
                    InFlightMinor: paid.Sum(o => refunds[o.Id].Where(r => r.State is RefundState.Pending or RefundState.ManualRequired).Sum(r => r.AmountMinor)),
                    StillRefundableMinor: paid.Sum(Refundable),
                    DonationsMinor: paid.Sum(o => o.TotalMinor - o.TicketTotalMinor),
                    RefundsSince: since.GroupBy(r => r.State)
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
    long ChargedBackMinor,
    long RefundedMinor,
    long InFlightMinor,
    long StillRefundableMinor,
    long DonationsMinor,
    IReadOnlyList<RefundStateRow> RefundsSince);
