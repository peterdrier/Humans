using System.Security.Cryptography;
using System.Text;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Base.Extensions;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>Issued tickets and the resale queue: return, withdraw, directed resale, and the refund when one sells.</summary>
internal sealed partial class TicketCheckoutService
{
    private const int MaxInvitesPerTicket = 3;
    private static readonly Duration DeliveryGrace = Duration.FromMinutes(30);
    private static readonly Duration DeliveryRepairWindow = Duration.FromDays(7);

    public async Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForOrderAsync(Guid orderId, CancellationToken ct = default) =>
        (await repository.GetTicketsForOrderAsync(orderId, ct)).OrderBy(t => t.Kind).ThenBy(t => t.IssuedAt).ToList();

    public Task<TicketCheckoutTicket?> GetTicketAsync(Guid id, CancellationToken ct = default) =>
        repository.GetTicketAsync(id, ct);

    public Task RecordDeliveryAsync(
        Guid orderId, TicketCheckoutOrderEventKind kind, string detail, Guid? actorUserId, CancellationToken ct = default)
    {
        if (kind is not (TicketCheckoutOrderEventKind.TicketsEmailed or TicketCheckoutOrderEventKind.TicketsResent
            or TicketCheckoutOrderEventKind.TicketMailSkipped))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only delivery steps are recorded here.");
        return TimelineAsync(orderId, kind, Truncate(detail), actorUserId, ct);
    }

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
            var queue = offers.Where(o => o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved).ToList();
            var positions = TicketSupportReports.QueuePositions(offers);
            var eventTickets = salesEvent is null ? [] : await repository.GetTicketsForSalesEventAsync(group.Key, ct);
            foreach (var ticket in group.OrderBy(t => t.IssuedAt))
            {
                var offer = queue.FirstOrDefault(o => o.TicketId == ticket.Id);
                var hasDependents = eventTickets.Any(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid);
                held.Add(new HeldTicket(ticket, eventName, offer, offer is null ? null : positions[offer.Id],
                    CanOffer: salesEvent?.EffectiveSwitches.Resale is not (null or ResaleScheme.Off) && offer is null && ticket.Kind == TicketHolderKind.Adult && salesEvent is { Mode: not TicketSalesMode.Closed },
                    HasDependents: hasDependents,
                    DirectedActive: offer?.HasLiveInvite == true)
                {
                    CanInvite = salesEvent?.EffectiveSwitches.Resale == ResaleScheme.QueueAndDirected,
                });
                // A child ticket has no account of its own: it shows under the adult who holds it.
                foreach (var child in eventTickets.Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid))
                    held.Add(new HeldTicket(child, eventName, null, null, CanOffer: false, HasDependents: false, DirectedActive: false));
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
        if (salesEvent is null || salesEvent.Mode == TicketSalesMode.Closed || salesEvent.EffectiveSwitches.Resale == ResaleScheme.Off)
            return new("Tickets_Resale_Closed");
        // The bank already took this order's money back: a resale would owe a refund of money Humans no longer holds.
        if (await repository.GetOrderAsync(ticket.OrderId, ct) is { DisputedMinor: > 0 })
            return new("Tickets_Resale_NotOfferable");

        // Children go wherever their adult goes: returned with the adult and refunded with it when it sells.
        // Event-wide: an admin-issued companion lives on its own order.
        var dependents = (await repository.GetTicketsForSalesEventAsync(ticket.SalesEventId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .ToList();
        if (await IsAdmittedAsync(ticket, ct))
            return new("Tickets_Resale_NotOfferable");
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
        offer.DirectedEmail = null;
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
        var salesEvent = ticket is null ? null : await repository.GetSalesEventAsync(ticket.SalesEventId, ct);
        if (ticket is null || offer is null || salesEvent?.EffectiveSwitches.Resale != ResaleScheme.QueueAndDirected)
            return new("Tickets_Resale_NotFound");
        if (offer.State != TicketResaleOfferState.Open)
            return new("Tickets_Resale_InCheckout");
        if (await IsAdmittedAsync(ticket, ct))
            return new("Tickets_Resale_NotOfferable");
        var now = clock.GetCurrentInstant();
        if (offer.HasLiveInvite)
            return new("Tickets_Resale_InviteLive");
        // Counted over all of the ticket's offers, so withdrawing and offering again does not start a fresh allowance.
        if ((await repository.GetOffersForSalesEventAsync(ticket.SalesEventId, ct))
                .Where(o => o.TicketId == ticket.Id).Sum(o => o.DirectedInviteCount) >= MaxInvitesPerTicket)
            return new("Tickets_Resale_InviteLimit");
        inviteeEmail = inviteeEmail.Trim();
        if (!LooksLikeEmail(inviteeEmail) || string.Equals(inviteeEmail, ticket.HolderEmail, StringComparison.OrdinalIgnoreCase))
            return new("Tickets_Resale_InviteEmailInvalid");
        if (!(await LoadStockAsync(salesEvent, now, ct)).ResaleOpen)
            return new("Tickets_Resale_NotOpenYet");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        offer.DirectedEmail = inviteeEmail;
        offer.DirectedTokenHash = HashToken(token);
        offer.DirectedUsedAt = null;
        offer.DirectedInviteCount++;
        await repository.UpdateOfferAsync(offer, ct);

        var eventName = (await settings.GetEventSettingsByIdAsync(salesEvent.EventSettingsId, ct))?.EventName ?? string.Empty;
        await email.SendAsync(emails.ResaleInvitation(
            inviteeEmail, ticket.HolderName, eventName, new Uri(claimUrlBase, token).ToString(), culture), ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInvited,
            $"Holder invited one buyer for ticket {ticket.Barcode}; the ticket stays in the queue meanwhile",
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
        offer.DirectedEmail = null;
        await repository.UpdateOfferAsync(offer, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInviteCancelled,
            $"Invitation for ticket {ticket.Barcode} cancelled; it keeps its place in the queue", userId, ct);
        return CheckoutMutationResult.Ok;
    }

    /// <summary>The ticket or a valid child riding on it was admitted: selling it would void someone already inside.</summary>
    private async Task<bool> IsAdmittedAsync(TicketCheckoutTicket ticket, CancellationToken ct)
    {
        var dependents = (await repository.GetTicketsForOrderAsync(ticket.OrderId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .Select(t => t.Barcode);
        return (await gateAdmissions.GetAdmittedAsync([ticket.Barcode, .. dependents], ct)).Count > 0;
    }

    private async Task<bool> IsAdmittedAsync(Guid ticketId, CancellationToken ct) =>
        await repository.GetTicketAsync(ticketId, ct) is { } ticket && await IsAdmittedAsync(ticket, ct);

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
        context.SalesEvent.ResaleSelling
        && offer.State == TicketResaleOfferState.Open && offer.HasLiveInvite
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
            if ((await repository.GetSalesEventAsync(offer.SalesEventId, ct))?.EffectiveSwitches.Resale == ResaleScheme.Off)
            {
                await WithdrawSwitchedOffAsync(offer, null, ct);
                continue;
            }
            offer.State = TicketResaleOfferState.Open;
            offer.ReservedByOrderId = null;
            await repository.UpdateOfferAsync(offer, ct);
        }
    }

    /// <summary>
    /// Issues the paid order's tickets (once: the unique line index turns a second caller away), completes any resale
    /// it bought, and tells delivery. Safe to repeat: the reconcile job calls it again for a paid order a crash left
    /// without tickets or with a resale unfinished.
    /// </summary>
    private async Task CompletePaidAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        var issuedNow = false;
        if ((await repository.GetTicketsForOrderAsync(order.Id, ct)).Count == 0
            && order.Lines.Any(l => l.Kind == CheckoutLineKind.Ticket))
        {
            await ResolvePartnersAsync(order, ct);
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
                    LineIndex = x.index,
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
            if (!await repository.AddTicketsAsync(tickets, ct))
                return;
            foreach (var holder in tickets.Select(t => t.HolderUserId).OfType<Guid>().Distinct())
                holdings.InvalidateHoldings(holder);
            issuedNow = true;
            await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.TicketsIssued,
                $"{tickets.Count} ticket(s) issued: {string.Join(", ", tickets.Select(t => t.Barcode))}", null, ct);
        }

        foreach (var offerId in order.Lines.Select(l => l.ResaleOfferId).OfType<Guid>())
            await CompleteResaleAsync(offerId, order, ct);

        if (issuedNow)
            await HandOffToDeliveryAsync(order.Id, ct);
    }

    private async Task HandOffToDeliveryAsync(Guid orderId, CancellationToken ct)
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

    /// <summary>Finds or makes each partner's account now the order is paid (see <see cref="ResolveAccountAsync"/>).</summary>
    private async Task ResolvePartnersAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        var changed = false;
        foreach (var line in order.Lines.Where(l =>
                     l is { Kind: CheckoutLineKind.Ticket, AttendeeUserId: null, AttendeeEmail: not null } && l.HolderKind != TicketHolderKind.Child))
        {
            Guid? holder = null;
            string? problem = null;
            try
            {
                (holder, problem) = await ResolveAccountAsync(line.AttendeeEmail!, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Partner account for order {OrderId} could not be resolved", order.Id);
                problem = "finding or creating the account failed";
            }

            if (holder is { } holderId)
            {
                line.AttendeeUserId = holderId;
                changed = true;
                if (await HoldsTicketAsync(order.SalesEventId, holderId, clock.GetCurrentInstant(), ct))
                    await NeedsAttentionAsync(order.Id,
                        "The partner already held a ticket for this event; the ticketing team should refund one", ct);
            }
            else
            {
                await NeedsAttentionAsync(order.Id,
                    $"Partner ticket issued without an account because {problem}; the ticketing team must attach it", ct);
            }
        }
        if (changed)
            await repository.UpdateOrderAsync(order, ct);
    }

    /// <summary>
    /// The account a ticket for <paramref name="email"/> belongs to. Only a verified address attaches a ticket to an
    /// existing account; a single unverified row is replaced by a new account, as the attendee import does; an
    /// ambiguous address gives no account and the reason. The legal name stays on the line and ticket: as a display
    /// name it would become the account's public burner name.
    /// </summary>
    private async Task<(Guid? UserId, string? Problem)> ResolveAccountAsync(string email, CancellationToken ct)
    {
        var verified = (await userEmails.FindByAddressAsync(email, aliased: true, verifiedOnly: true, ct))
            .Select(r => r.UserId).Distinct().ToList();
        if (verified.Count == 1)
            return ((await users.GetUserInfoAsync(verified[0], ct))?.Id ?? verified[0], null);
        if (verified.Count > 1)
            return (null, "the address is verified on more than one account");
        var rows = await userEmails.FindByAddressAsync(email, aliased: true, verifiedOnly: false, ct);
        if (rows.Count > 1)
            return (null, "the address is unverified on more than one account");
        var provisioned = rows.Count == 1
            ? await provisioning.ReplaceUnverifiedEmailAndProvisionAsync(rows[0].UserId, rows[0].Id, email, displayName: null, ContactSource.Manual, ct)
            : await provisioning.FindOrCreateUserByEmailAsync(email, displayName: null, ContactSource.Manual, ct);
        return (provisioned.UserId, null);
    }

    /// <summary>On the order's timeline and in the audit log, where the ticketing team reviews what automation left them.</summary>
    private async Task NeedsAttentionAsync(Guid orderId, string detail, CancellationToken ct)
    {
        await TimelineAsync(orderId, TicketCheckoutOrderEventKind.TicketsIssued, detail, null, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, AuditEntityType, orderId, detail, JobName);
    }

    private async Task CompleteResaleAsync(Guid offerId, TicketCheckoutOrder buyerOrder, CancellationToken ct)
    {
        var offer = await repository.GetOfferAsync(offerId, ct);
        if (offer is null)
            return;
        if (offer.State == TicketResaleOfferState.Sold)
        {
            // A repeat after a crash finishes whatever the first run left: the old ticket, its children, the refund.
            if (offer.SoldToOrderId == buyerOrder.Id)
            {
                await RetireResoldTicketAsync(offer, offer.DirectedUsedAt is not null, ct);
                await SettleResaleRefundAsync(offer, ct);
            }
            return;
        }
        // A late payment finds its offer released, maybe taken by another checkout or withdrawn: its ticket is then fresh stock.
        if (offer.State == TicketResaleOfferState.Withdrawn
            || (offer.State == TicketResaleOfferState.Reserved && offer.ReservedByOrderId != buyerOrder.Id))
            return;

        var now = clock.GetCurrentInstant();
        // The queue also sells an invited ticket, so only the invited buyer's own purchase counts as using the invitation.
        var directed = offer.HasLiveInvite
            && string.Equals(offer.DirectedEmail, buyerOrder.BuyerEmail, StringComparison.OrdinalIgnoreCase);
        offer.State = TicketResaleOfferState.Sold;
        offer.SoldAt = now;
        offer.SoldToOrderId = buyerOrder.Id;
        offer.ReservedByOrderId = null;
        if (directed)
            offer.DirectedUsedAt = now;
        offer.DirectedEmail = null;
        await repository.UpdateOfferAsync(offer, ct);

        await RetireResoldTicketAsync(offer, directed, ct);
        if (directed)
            await TimelineAsync(buyerOrder.Id, TicketCheckoutOrderEventKind.ResaleInviteUsed,
                "Bought through a resale invitation", buyerOrder.BuyerUserId, ct);

        await SettleResaleRefundAsync(offer, ct);
    }

    /// <summary>Marks a sold offer's ticket <c>Resold</c> and voids its children; each step is skipped once done.</summary>
    private async Task RetireResoldTicketAsync(TicketResaleOffer offer, bool directed, CancellationToken ct)
    {
        if (await repository.GetTicketAsync(offer.TicketId, ct) is not { } old)
            return;
        var now = clock.GetCurrentInstant();
        if (old.State != TicketCheckoutTicketState.Resold)
        {
            old.State = TicketCheckoutTicketState.Resold;
            await repository.UpdateTicketAsync(old, ct);
            await TimelineAsync(old.OrderId, TicketCheckoutOrderEventKind.ResaleSold,
                $"Ticket {old.Barcode} resold" + (directed ? " to the invited buyer" : string.Empty) +
                $"; refunding {FormatMinor(offer.RefundOwedMinor)}", null, ct);
        }
        foreach (var child in (await repository.GetTicketsForSalesEventAsync(old.SalesEventId, ct))
                     .Where(t => t.ParentTicketId == old.Id && t.State == TicketCheckoutTicketState.Valid))
        {
            child.State = TicketCheckoutTicketState.Void;
            child.VoidedAt = now;
            child.VoidReason = "Returned with the adult's ticket";
            await repository.UpdateTicketAsync(child, ct);
            if (child.OrderId != old.OrderId)
                await TimelineAsync(child.OrderId, TicketCheckoutOrderEventKind.Voided,
                    $"Ticket {child.Barcode} voided: its adult's ticket {old.Barcode} was resold", null, ct);
        }
    }

    /// <summary>
    /// Refunds the holder what they paid, under the offer's own id so a repeat finds the same refund and retries a
    /// backend call that threw. A refund that cannot even start is written as a failed refund on the holder's order,
    /// so the ticketing team sees what is owed.
    /// </summary>
    private async Task SettleResaleRefundAsync(TicketResaleOffer offer, CancellationToken ct)
    {
        if (offer.RefundOwedMinor <= 0)
            return;
        if (await repository.GetTicketAsync(offer.TicketId, ct) is not { } old)
            return;

        var (result, refundId) = await RefundCoreAsync(old.OrderId, offer.RefundOwedMinor, "Ticket resold", null, ct, offer.Id);
        if (refundId is null)
        {
            logger.LogError("Resale refund for ticket {TicketId} could not start: {Refusal}", old.Id, result.Refusal);
            await repository.AddRefundAsync(new TicketCheckoutRefund
            {
                Id = offer.Id,
                OrderId = old.OrderId,
                AmountMinor = offer.RefundOwedMinor,
                Reason = "Ticket resold",
                State = RefundState.Failed,
                FailureReason = Truncate(result.Refusal ?? "Refused"),
                CreatedAt = clock.GetCurrentInstant(),
            }, ct);
            await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, AuditEntityType, old.OrderId,
                $"Resale refund of {FormatMinor(offer.RefundOwedMinor)} could not start ({Truncate(result.Refusal ?? "refused")}); " +
                "the ticketing team must refund it by hand", JobName);
            await TimelineAsync(old.OrderId, TicketCheckoutOrderEventKind.RefundSettled,
                $"Resale refund of {FormatMinor(offer.RefundOwedMinor)} could not start ({Truncate(result.Refusal ?? "refused")}); " +
                "the ticketing team must refund it by hand", null, ct);
        }
        if (offer.RefundId is null)
        {
            offer.RefundId = offer.Id;
            await repository.UpdateOfferAsync(offer, ct);
        }
    }

    /// <summary>Finishes what a crash left behind: paid orders without their tickets, resale refund or delivery, and refunds that never reached the backend.</summary>
    private async Task<int> RepairPaidAsync(CancellationToken ct)
    {
        var repaired = 0;
        foreach (var salesEvent in await repository.GetSalesEventsAsync(ct))
        {
            var issued = (await repository.GetTicketsForSalesEventAsync(salesEvent.Id, ct)).Select(t => t.OrderId).ToHashSet();
            var orders = await repository.GetOrdersForSalesEventAsync(salesEvent.Id, ct);
            var unfinished = (await repository.GetOffersForSalesEventAsync(salesEvent.Id, ct))
                .Where(o => o is { State: TicketResaleOfferState.Sold, RefundId: null, RefundOwedMinor: > 0 })
                .Select(o => o.SoldToOrderId)
                .ToHashSet();
            foreach (var order in orders.Where(o => o.State == TicketCheckoutOrderState.Paid
                         && ((!issued.Contains(o.Id) && o.Lines.Any(l => l.Kind == CheckoutLineKind.Ticket)) || unfinished.Contains(o.Id))))
            {
                try
                {
                    await CompletePaidAsync(order, ct);
                    repaired++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Finishing paid checkout order {OrderId} failed; next run retries", order.Id);
                }
            }

            // Tickets issued but never handed to delivery (the process stopped in between). Delivery dedupes a repeat;
            // the wait lets a first hand-off still in its queue finish, and the window keeps old orders out.
            var now = clock.GetCurrentInstant();
            foreach (var order in orders.Where(o => deliveries.Any() && o is { State: TicketCheckoutOrderState.Paid, PaidAt: { } paidAt }
                         && issued.Contains(o.Id) && paidAt <= now - DeliveryGrace && paidAt > now - DeliveryRepairWindow))
            {
                if ((await repository.GetOrderEventsAsync(order.Id, ct)).Any(e =>
                        e.Kind is TicketCheckoutOrderEventKind.TicketsEmailed or TicketCheckoutOrderEventKind.TicketMailSkipped))
                    continue;
                await HandOffToDeliveryAsync(order.Id, ct);
            }

            // A refund whose backend call threw may or may not exist there; repeating it under its id settles which.
            foreach (var unsent in (await repository.GetRefundsForSalesEventAsync(salesEvent.Id, ct))
                         .Where(r => r is { State: RefundState.Pending, BackendRefundId: null }))
            {
                try
                {
                    await RefundCoreAsync(unsent.OrderId, unsent.AmountMinor, unsent.Reason, null, ct, unsent.Id);
                    if ((await repository.GetRefundsAsync(unsent.OrderId, ct)).FirstOrDefault(r => r.Id == unsent.Id)
                        is not { State: RefundState.Pending, BackendRefundId: null })
                        repaired++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Resending refund {RefundId} failed; next run retries", unsent.Id);
                }
            }
        }
        return repaired;
    }

    /// <summary>
    /// Repairs offers held by a checkout that will never pay or already paid. Returns how many changed.
    /// </summary>
    private async Task<int> SweepOffersAsync(CancellationToken ct)
    {
        var changed = 0;
        foreach (var salesEvent in await repository.GetSalesEventsAsync(ct))
        {
            foreach (var offer in await repository.GetOffersForSalesEventAsync(salesEvent.Id, ct))
            {
                if (offer is { State: TicketResaleOfferState.Reserved, ReservedByOrderId: { } orderId }
                         && await repository.GetOrderAsync(orderId, ct) is { } holder
                         && !holder.IsOpen)
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
