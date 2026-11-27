using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

namespace Humans.Tickets.Services.Checkout;

/// <summary>Issued tickets and the resale queue: offer, withdraw, directed resale, and the evening refund batch.</summary>
internal sealed partial class TicketCheckoutService
{
    /// <summary>Links a holder may send for one ticket over all its offers: enough for friends, too few to spam with.</summary>
    internal const int MaxInvitesPerTicket = 10;

    /// <summary>How long a resale buyer's payment must stand before the evening batch refunds the old holder.</summary>
    internal static readonly Duration ResaleRefundHold = Duration.FromHours(12);

    private const string RefundHeldBack = "Resale refund held back";
    private static readonly Duration DeliveryGrace = Duration.FromMinutes(30);
    private static readonly Duration DeliveryRepairWindow = Duration.FromDays(7);

    public Task<TicketCheckoutTicket?> GetTicketAsync(Guid id, CancellationToken ct = default) =>
        repository.GetTicketAsync(id, ct);

    public async Task RecordDeliveryAsync(
        Guid orderId, TicketCheckoutOrderEventKind kind, string detail, Guid? actorUserId,
        CancellationToken ct = default, Guid? resaleOfferId = null)
    {
        if (resaleOfferId is { } offerId)
        {
            if (kind is not (TicketCheckoutOrderEventKind.ResaleSold or TicketCheckoutOrderEventKind.ResaleUnsold))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only resale notices reference an offer.");
            var offer = await repository.GetOfferAsync(offerId, ct);
            if (offer is null || (await repository.GetTicketAsync(offer.TicketId, ct))?.OrderId != orderId)
                throw new ArgumentException("The resale offer does not belong to this order.", nameof(resaleOfferId));
            var sold = kind == TicketCheckoutOrderEventKind.ResaleSold;
            if (offer.State != (sold ? TicketResaleOfferState.Sold : TicketResaleOfferState.Open)
                || (sold ? offer.SoldNotificationPending == false : offer.UnsoldNotifiedAt is not null))
                return;
            await TimelineAsync(orderId, kind, Truncate(detail), actorUserId, ct);
            if (sold)
                offer.SoldNotificationPending = false;
            else
                offer.UnsoldNotifiedAt = clock.GetCurrentInstant();
            await repository.UpdateOfferAsync(offer, ct);
            return;
        }
        if (kind is not (TicketCheckoutOrderEventKind.TicketsEmailed or TicketCheckoutOrderEventKind.TicketsResent
            or TicketCheckoutOrderEventKind.TicketMailSkipped))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only delivery steps are recorded here.");
        await TimelineAsync(orderId, kind, Truncate(detail), actorUserId, ct);
    }

    public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForHolderAsync(Guid userId, CancellationToken ct = default) =>
        repository.GetTicketsForHolderAsync(userId, ct);

    public async Task<int> GetHeldTicketCountAsync(Guid userId, CancellationToken ct = default)
    {
        var activeEvent = settings.GetActiveEventSettings();
        var forEvent = (await repository.ListTicketingSettingsAsync(ct))
            .Where(e => activeEvent is null || e.EventSettingsId == activeEvent.Id || e.Mode != TicketSalesMode.Closed)
            .Select(e => e.Id).ToHashSet();
        return (await repository.GetTicketsForHolderAsync(userId, ct))
            .Count(t => t.IsHeld && forEvent.Contains(t.TicketingSettingsId));
    }

    public Task<IReadOnlyList<TicketCheckoutTicket>> GetTicketsForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
        repository.GetTicketsForSettingsAsync(settingsId, ct);

    public Task<IReadOnlyList<TicketResaleOffer>> GetOffersForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
        repository.GetOffersForSettingsAsync(settingsId, ct);

    public Task<IReadOnlyList<TicketCheckoutRefund>> GetRefundsForSettingsAsync(Guid settingsId, CancellationToken ct = default) =>
        repository.GetRefundsForSettingsAsync(settingsId, ct);

    public async Task<IReadOnlyList<HeldTicket>> GetHeldTicketsAsync(Guid userId, CancellationToken ct = default)
    {
        var held = new List<HeldTicket>();
        var now = clock.GetCurrentInstant();
        foreach (var group in (await repository.GetTicketsForHolderAsync(userId, ct))
                     .Where(t => t.IsHeld)
                     .GroupBy(t => t.TicketingSettingsId))
        {
            var ticketing = await repository.GetTicketingSettingsAsync(group.Key, ct);
            var eventName = ticketing is null ? string.Empty
                : (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct))?.EventName ?? string.Empty;
            var offers = await repository.GetOffersForSettingsAsync(group.Key, ct);
            var queue = offers.Where(o => o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved).ToList();
            var positions = TicketSupportReports.QueuePositions(offers);
            var eventTickets = ticketing is null ? [] : await repository.GetTicketsForSettingsAsync(group.Key, ct);
            var companionRequests = await repository.GetCompanionRequestsForSettingsAsync(group.Key, ct);
            foreach (var ticket in group)
            {
                var offer = queue.FirstOrDefault(o => o.TicketId == ticket.Id);
                var hasDependents = eventTickets.Any(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid);
                held.Add(new HeldTicket(ticket, eventName, offer, offer is null ? null : positions[offer.Id],
                    CanOffer: ticketing is { Mode: not TicketSalesMode.InvitedOnly } && !ticketing.IsClosed(now)
                        && ticketing.EffectiveSwitches.Resale != ResaleScheme.Off && offer is null && ticket.Kind == TicketHolderKind.Adult,
                    HasDependents: hasDependents,
                    DirectedActive: offer?.HasLiveInvite == true)
                {
                    CanInvite = ticketing?.EffectiveSwitches.Resale == ResaleScheme.QueueAndDirected && ticketing.IsResaleSelling(now),
                    CompanionRequest = companionRequests.Where(r => r.TicketId == ticket.Id).MaxBy(r => r.RequestedAt),
                    CanAskCompanion = CanAskCompanion(ticket, ticketing, eventTickets, companionRequests),
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
        if (ticket.State != TicketCheckoutTicketState.Valid || ticket.Kind != TicketHolderKind.Adult || ticket.VoidRefundId is not null)
            return new("Tickets_Resale_NotOfferable");
        if ((await repository.GetOffersForSettingsAsync(ticket.TicketingSettingsId, ct))
            .Any(o => o.TicketId == ticket.Id && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved))
            return new("Tickets_Resale_NotOfferable");
        var ticketing = await repository.GetTicketingSettingsAsync(ticket.TicketingSettingsId, ct);
        var now = clock.GetCurrentInstant();
        if (ticketing is null || ticketing.IsClosed(now) || ticketing.Mode == TicketSalesMode.InvitedOnly
            || ticketing.EffectiveSwitches.Resale == ResaleScheme.Off)
            return new("Tickets_Resale_Closed");
        // The bank already took this order's money back: a resale would owe a refund of money Humans no longer holds.
        if (await repository.GetOrderAsync(ticket.OrderId, ct) is { ChargedBackMinor: > 0 })
            return new("Tickets_Resale_NotOfferable");

        // Children go wherever their adult goes: put up for resale with the adult and refunded with it when it sells.
        // Event-wide: an admin-issued companion lives on its own order.
        var dependents = (await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .ToList();
        if (dependents.Any(t => t.VoidRefundId is not null) || await IsAdmittedAsync(ticket, ct))
            return new("Tickets_Resale_NotOfferable");
        now = clock.GetCurrentInstant();
        if (ticketing.IsClosed(now))
            return new("Tickets_Resale_Closed");
        var offer = new TicketResaleOffer
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticket.TicketingSettingsId,
            TicketId = ticket.Id,
            HolderUserId = userId,
            OfferedAt = now,
            State = TicketResaleOfferState.Open,
            RefundOwedMinor = ticket.PricePaidMinor + dependents.Sum(d => d.PricePaidMinor),
        };
        ticket.State = TicketCheckoutTicketState.Offered;
        await repository.AddOfferAsync(offer, ct, ticket);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleOffered,
            $"Ticket {ticket.Barcode} put up for resale; {FormatMinor(offer.RefundOwedMinor)} owed if it sells" +
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
        ticket.State = TicketCheckoutTicketState.Valid;
        await repository.UpdateTicketAsync(ticket, ct, offer: offer);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleWithdrawn,
            $"Ticket {ticket.Barcode} taken back out of the resale queue", userId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketResaleWithdrawn, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} withdrawn from resale", userId);
        return CheckoutMutationResult.Ok;
    }

    /// <summary>
    /// Queues new and pending invitations with stable delivery ids. Completed addresses are skipped; retry links
    /// authenticate the same invitation so the outbox's first payload remains valid.
    /// </summary>
    public async Task<CheckoutMutationResult> InviteBuyersAsync(
        Guid ticketId, Guid userId, IReadOnlyList<string> inviteeEmails, Uri claimUrlBase, string culture, CancellationToken ct = default)
    {
        var (ticket, offer) = await OwnOfferAsync(ticketId, userId, ct);
        var ticketing = ticket is null ? null : await repository.GetTicketingSettingsAsync(ticket.TicketingSettingsId, ct);
        if (ticket is null || offer is null || ticketing?.EffectiveSwitches.Resale != ResaleScheme.QueueAndDirected)
            return new("Tickets_Resale_NotFound");
        if (offer.State != TicketResaleOfferState.Open)
            return new("Tickets_Resale_InCheckout");
        if (await IsAdmittedAsync(ticket, ct))
            return new("Tickets_Resale_NotOfferable");
        var addresses = inviteeEmails.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addresses.Count == 0 || addresses.Any(e => !LooksLikeEmail(e) || string.Equals(e, ticket.HolderEmail, StringComparison.OrdinalIgnoreCase)))
            return new("Tickets_Resale_InviteEmailInvalid");
        var newAddresses = addresses.Where(e => !offer.Invites.Any(i => string.Equals(i.Email, e, StringComparison.OrdinalIgnoreCase))).ToList();
        var pending = offer.Invites.Where(i => i is { DeliveryPending: true, DeliveryId: not null }
            && addresses.Contains(i.Email, StringComparer.OrdinalIgnoreCase)).ToList();
        if (newAddresses.Count == 0 && pending.Count == 0)
            return new("Tickets_Resale_InviteAlready");
        // Retrying an existing invitation neither resets its allowance nor invalidates a possibly queued link.
        if ((await repository.GetOffersForSettingsAsync(ticket.TicketingSettingsId, ct))
                .Where(o => o.TicketId == ticket.Id).Sum(o => o.DirectedInviteCount) + newAddresses.Count > MaxInvitesPerTicket)
            return new("Tickets_Resale_InviteLimit");
        var now = clock.GetCurrentInstant();
        var stock = await LoadStockAsync(ticketing, now, ct);
        now = clock.GetCurrentInstant();
        if (ticketing.IsClosed(now))
            return new("Tickets_Resale_Closed");
        if (!stock.ResaleOpen)
            return new("Tickets_Resale_NotOpenYet");

        var links = pending.Select(i => (Invite: i, Token: InvitationToken(offer.Id, i.DeliveryId!.Value))).ToList();
        foreach (var address in newAddresses)
        {
            var deliveryId = Guid.NewGuid();
            var token = InvitationToken(offer.Id, deliveryId);
            var invite = new TicketResaleInvite
            {
                Email = address,
                TokenHash = HashToken(token),
                SentAt = now,
                DeliveryId = deliveryId,
                DeliveryPending = true,
            };
            offer.Invites.Add(invite);
            links.Add((invite, token));
        }
        offer.DirectedInviteCount += newAddresses.Count;
        await repository.UpdateOfferAsync(offer, ct);

        var eventName = (await settings.GetEventSettingsByIdAsync(ticketing.EventSettingsId, ct))?.EventName ?? string.Empty;
        foreach (var (invite, token) in links)
        {
            await email.SendAsync(emails.ResaleInvitation(
                invite.Email, ticket.HolderName, eventName, new Uri(claimUrlBase, token).ToString(), culture)
                with
            { DeliveryId = invite.DeliveryId }, ct);
            invite.DeliveryPending = false;
            await repository.UpdateOfferAsync(offer, ct);
        }
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInvited,
            $"Holder sent the link for ticket {ticket.Barcode} to {links.Count} person(s), {offer.Invites.Count} in all; " +
            "the ticket stays in the queue meanwhile", userId, ct);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> CancelInviteAsync(Guid ticketId, Guid userId, CancellationToken ct = default)
    {
        var (ticket, offer) = await OwnOfferAsync(ticketId, userId, ct);
        if (ticket is null || offer is null || offer.Invites.Count == 0)
            return new("Tickets_Resale_NotFound");
        if (offer.State == TicketResaleOfferState.Reserved)
            return new("Tickets_Resale_InCheckout");
        offer.Invites = [];
        await repository.UpdateOfferAsync(offer, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.ResaleInviteCancelled,
            $"Invitations for ticket {ticket.Barcode} cancelled; it keeps its place in the queue", userId, ct);
        return CheckoutMutationResult.Ok;
    }

    /// <summary>The ticket or a valid dependent was admitted: a companion can be on a separate order.</summary>
    private async Task<bool> IsAdmittedAsync(TicketCheckoutTicket ticket, CancellationToken ct)
    {
        var dependents = (await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
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
        if (ticket is null || ticket.HolderUserId != userId || ticket.State != TicketCheckoutTicketState.Offered || ticket.VoidRefundId is not null)
            return (null, null);
        var offer = (await repository.GetOffersForSettingsAsync(ticket.TicketingSettingsId, ct))
            .FirstOrDefault(o => o.TicketId == ticket.Id && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved);
        return (ticket, offer);
    }

    /// <summary>
    /// Why the link can't buy for the signed-in buyer, or null when it was sent to their email, for this event, and
    /// resale is selling.
    /// </summary>
    private static string? InviteRefusal(TicketResaleOffer? offer, string tokenHash, BuyContext context) =>
        offer is null || offer.TicketingSettingsId != context.Ticketing.Id || offer.InviteFor(tokenHash) is not { } invite
            ? "Tickets_Resale_InviteNotFound"
            : !string.Equals(invite.Email, context.Buyer?.Email, StringComparison.OrdinalIgnoreCase)
                ? "Tickets_Resale_InviteOtherEmail"
                : !context.Ticketing.IsResaleSelling(context.Now) ? "Tickets_Resale_NotSelling" : null;

    /// <summary>The linked offer while it can still sell to this buyer: the link is theirs and the queue is open.</summary>
    private static TicketResaleOffer? DirectedOfferFor(TicketResaleOffer? offer, string tokenHash, BuyContext context) =>
        offer?.State == TicketResaleOfferState.Open && InviteRefusal(offer, tokenHash, context) is null
            ? offer
            : null;

    private string InvitationToken(Guid offerId, Guid deliveryId) =>
        _resaleLinks.Protect($"{offerId:D}/{deliveryId:D}");

    private async Task<(TicketResaleOffer? Offer, string TokenHash)> ResolveInvitationAsync(string token, CancellationToken ct)
    {
        var hash = HashToken(token);
        if (await repository.GetOfferByTokenHashAsync(hash, ct) is { } legacy)
            return (legacy, hash);
        string payload;
        try
        {
            payload = _resaleLinks.Unprotect(token);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return (null, hash);
        }
        var parts = payload.Split('/');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var offerId) || !Guid.TryParse(parts[1], out var deliveryId))
            return (null, hash);
        var offer = await repository.GetOfferAsync(offerId, ct);
        var invite = offer?.Invites.FirstOrDefault(i => i.DeliveryId == deliveryId);
        return invite is null ? (null, hash) : (offer, invite.TokenHash);
    }

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
            if ((await repository.GetTicketingSettingsAsync(offer.TicketingSettingsId, ct))?.EffectiveSwitches.Resale == ResaleScheme.Off)
            {
                await WithdrawResaleOfferAsync(offer, null, ct);
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
            && order.Lines.Any(l => l.Kind == OrderLineKind.Ticket))
        {
            await FlagDoubleHoldersAsync(order, ct);
            var now = clock.GetCurrentInstant();
            var ids = order.Lines.Select(_ => Guid.NewGuid()).ToList();
            var tickets = order.Lines
                .Select((line, index) => (line, index))
                .Where(x => x.line.Kind == OrderLineKind.Ticket)
                .Select(x => new TicketCheckoutTicket
                {
                    Id = ids[x.index],
                    TicketingSettingsId = order.TicketingSettingsId,
                    OrderId = order.Id,
                    LineIndex = x.index,
                    Kind = x.line.HolderKind ?? TicketHolderKind.Adult,
                    ParentTicketId = x.line.ParentLineIndex is { } parent ? ids[parent] : null,
                    HolderUserId = x.line.HolderUserId,
                    HolderName = x.line.HolderName ?? string.Empty,
                    HolderEmail = x.line.HolderEmail,
                    Barcode = NewBarcode(),
                    PricePaidMinor = x.line.AmountMinor,
                    WaveName = x.line.WaveName ?? string.Empty,
                    ClassProgramId = x.line.ClassProgramId,
                    ClassLabel = x.line.ClassLabel,
                    DiscountsMinor = x.line.DiscountsMinor,
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

        if (issuedNow || !order.Lines.Any(l => l.Kind == OrderLineKind.Ticket))
            await HandOffToDeliveryAsync(order.Id, ct);
    }

    private Task HandOffToDeliveryAsync(Guid orderId, CancellationToken ct) =>
        TellDeliveryAsync(d => d.TicketsIssuedAsync(orderId, ct), orderId);

    /// <summary>A mail that cannot be queued is logged and never undoes the change it reports. False when one failed.</summary>
    private async Task<bool> TellDeliveryAsync(Func<ITicketDelivery, Task> tell, Guid subjectId)
    {
        var queued = true;
        foreach (var delivery in deliveries)
        {
            try
            {
                await tell(delivery);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ticket delivery for {SubjectId} failed", subjectId);
                queued = false;
            }
        }
        return queued;
    }

    /// <summary>
    /// Holding a ticket is checked before payment, but a lapsed order can still pay late after the person bought
    /// one of their own: the team then refunds one of the two.
    /// </summary>
    private async Task FlagDoubleHoldersAsync(TicketCheckoutOrder order, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        foreach (var (line, index) in order.Lines.Select((l, i) => (l, i + 1))
                     .Where(x => x.l is { Kind: OrderLineKind.Ticket, HolderUserId: not null } && x.l.HolderKind != TicketHolderKind.Child)
                     .DistinctBy(x => x.l.HolderUserId))
            if (await HoldsTicketAsync(order.TicketingSettingsId, line.HolderUserId!.Value, now, ct, order.Id))
                await NeedsAttentionAsync(order.Id,
                    $"The holder of line {index} already held a ticket for this event; the ticketing team should refund one", ct);
    }

    /// <summary>
    /// The account a ticket for <paramref name="address"/> belongs to. Only a verified address attaches a ticket to an
    /// existing account; a single unverified row is replaced by a new account, as the attendee import does; an
    /// ambiguous address gives no account and the reason. The legal name stays on the line and ticket: as a display
    /// name it would become the account's public burner name.
    /// </summary>
    private async Task<(Guid? UserId, string? Problem)> ResolveAccountAsync(string address, CancellationToken ct)
    {
        var verified = (await userEmails.FindByAddressAsync(address, aliased: true, verifiedOnly: true, ct))
            .Select(r => r.UserId).Distinct().ToList();
        if (verified.Count == 1)
            return ((await users.GetUserInfoAsync(verified[0], ct))?.Id ?? verified[0], null);
        if (verified.Count > 1)
            return (null, "the address is verified on more than one account");
        var rows = await userEmails.FindByAddressAsync(address, aliased: true, verifiedOnly: false, ct);
        if (rows.Count > 1)
            return (null, "the address is unverified on more than one account");
        var provisioned = rows.Count == 1
            ? await provisioning.ReplaceUnverifiedEmailAsync(rows[0].UserId, rows[0].Id, address, displayName: null, ContactSource.Manual, ct)
            : await provisioning.FindOrCreateUserByEmailAsync(address, displayName: null, ContactSource.Manual, ct);
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
            // A repeat after a crash finishes whatever the first run left: the old ticket and its children. The refund
            // is the evening batch's.
            if (offer.SoldToOrderId == buyerOrder.Id)
                await RetireResoldTicketAsync(offer, offer.DirectedUsedAt is not null, ct);
            return;
        }
        // A late payment finds its offer released, maybe taken by another order or withdrawn: its ticket is then fresh stock.
        if (offer.State == TicketResaleOfferState.Withdrawn
            || (offer.State == TicketResaleOfferState.Reserved && offer.ReservedByOrderId != buyerOrder.Id))
            return;

        if (await IsAdmittedAsync(offer.TicketId, ct))
        {
            await WithdrawResaleOfferAsync(offer, null, ct, "the holder or a dependent was already admitted");
            await NeedsAttentionAsync(buyerOrder.Id,
                "The resale ticket's holder or dependent was already admitted; the paid buyer received fresh stock instead, which may exceed capacity", ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        // The queue also sells an invited ticket, so only an invited buyer's own order counts as using the invitation.
        var directed = offer.HasLiveInvite
            && offer.Invites.Any(i => string.Equals(i.Email, buyerOrder.BuyerEmail, StringComparison.OrdinalIgnoreCase));
        offer.State = TicketResaleOfferState.Sold;
        offer.SoldAt = now;
        offer.SoldNotificationPending = true;
        offer.SoldToOrderId = buyerOrder.Id;
        offer.ReservedByOrderId = null;
        if (directed)
            offer.DirectedUsedAt = now;
        await repository.UpdateOfferAsync(offer, ct);

        await RetireResoldTicketAsync(offer, directed, ct);
        if (directed)
            await TimelineAsync(buyerOrder.Id, TicketCheckoutOrderEventKind.ResaleInviteUsed,
                "Bought through a resale invitation", buyerOrder.BuyerUserId, ct);
        await TellDeliveryAsync(d => d.ResaleSoldAsync(offer.TicketId, offer.RefundOwedMinor, ct), offer.TicketId);
    }

    /// <summary>Retires a sold adult and its valid dependents in one commit; repeats finish older partial retirements.</summary>
    private async Task RetireResoldTicketAsync(TicketResaleOffer offer, bool directed, CancellationToken ct)
    {
        if (await repository.GetTicketAsync(offer.TicketId, ct) is not { } old)
            return;
        var retireAdult = old.State != TicketCheckoutTicketState.Resold;
        var dependents = (await repository.GetTicketsForSettingsAsync(old.TicketingSettingsId, ct))
            .Where(t => t.ParentTicketId == old.Id && t.State == TicketCheckoutTicketState.Valid)
            .ToList();
        if (!retireAdult && dependents.Count == 0)
            return;
        var now = clock.GetCurrentInstant();
        old.State = TicketCheckoutTicketState.Resold;
        foreach (var child in dependents)
        {
            child.State = TicketCheckoutTicketState.Void;
            child.VoidedAt = now;
            child.VoidReason = "Resold with the adult's ticket";
        }
        await repository.UpdateTicketAsync(old, ct, dependents);
        if (retireAdult)
            await TimelineAsync(old.OrderId, TicketCheckoutOrderEventKind.ResaleSold,
                $"Ticket {old.Barcode} resold" + (directed ? " to the invited buyer" : string.Empty) +
                $"; {FormatMinor(offer.RefundOwedMinor)} goes back in the evening refund batch once the buyer's payment holds", null, ct);
        foreach (var child in dependents.Where(c => c.OrderId != old.OrderId))
            await TimelineAsync(child.OrderId, TicketCheckoutOrderEventKind.Voided,
                $"Ticket {child.Barcode} voided: its adult's ticket {old.Barcode} was resold", null, ct);
    }

    /// <summary>
    /// Refunds the holder what they paid, under the offer's own id so a repeat finds the same refund and retries a
    /// platform call that threw. A refund that cannot even start is written as a failed refund on the holder's order,
    /// so the ticketing team sees what is owed. True when the refund started.
    /// </summary>
    private async Task<bool> SettleResaleRefundAsync(TicketResaleOffer offer, CancellationToken ct)
    {
        if (offer.RefundOwedMinor <= 0 || await repository.GetTicketAsync(offer.TicketId, ct) is not { } old)
            return false;

        var (result, refundId) = await RefundCoreAsync(old.OrderId, offer.RefundOwedMinor, "Ticket resold", null, ct, offer.Id);
        if (refundId is null)
        {
            logger.LogError("Resale refund for ticket {TicketId} could not start: {Refusal}", old.Id, result.Refusal);
            var why = Truncate(result.Refusal ?? "refused");
            await RecordResaleRefundNotSentAsync(offer, old, why,
                $"Resale refund of {FormatMinor(offer.RefundOwedMinor)} could not start ({why}); the ticketing team must refund it by hand", ct);
            return false;
        }
        if (offer.RefundId is null)
        {
            offer.RefundId = offer.Id;
            await repository.UpdateOfferAsync(offer, ct);
        }
        return true;
    }

    /// <summary>
    /// A failed refund under the offer's id: the batch never sends it later, and it stays among the failed refunds the
    /// reconciliation lists until the team settles it.
    /// </summary>
    private async Task RecordResaleRefundNotSentAsync(
        TicketResaleOffer offer, TicketCheckoutTicket old, string failureReason, string detail, CancellationToken ct)
    {
        await repository.AddRefundAsync(new TicketCheckoutRefund
        {
            Id = offer.Id,
            OrderId = old.OrderId,
            AmountMinor = offer.RefundOwedMinor,
            Reason = "Ticket resold",
            State = RefundState.Failed,
            FailureReason = Truncate(failureReason),
            CreatedAt = clock.GetCurrentInstant(),
        }, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutNeedsAttention, AuditEntityType, old.OrderId, detail, JobName);
        await TimelineAsync(old.OrderId, TicketCheckoutOrderEventKind.RefundSettled, detail, null, ct);
        offer.RefundId = offer.Id;
        await repository.UpdateOfferAsync(offer, ct);
    }

    /// <summary>
    /// The evening batch: refunds each holder whose ticket sold in resale at least <see cref="ResaleRefundHold"/> ago to
    /// a buyer whose order is still paid with nothing refunded, being refunded or charged back. A sale that did not hold is left for
    /// the ticketing team as a failed refund. One audit row per event carries the batch's count and total.
    /// </summary>
    public async Task<ResaleRefundBatch> RunResaleRefundBatchAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetCurrentInstant() - ResaleRefundHold;
        var batch = new ResaleRefundBatch(0, 0, 0, 0);
        // A cancelled event refunds both the old holder's and the buyer's order in full, so a resale refund would pay twice.
        foreach (var ticketing in (await repository.ListTicketingSettingsAsync(ct)).Where(e => e.CancelledAt is null))
        {
            var (started, startedMinor, heldBack, waiting) = (0, 0L, 0, 0);
            foreach (var offer in (await repository.GetOffersForSettingsAsync(ticketing.Id, ct))
                         .Where(o => o is { State: TicketResaleOfferState.Sold, RefundId: null, RefundOwedMinor: > 0 })
                         .OrderBy(o => o.SoldAt))
            {
                if (offer.SoldAt > cutoff)
                {
                    waiting++;
                    continue;
                }
                var buyerOrder = offer.SoldToOrderId is { } buyerOrderId ? await repository.GetOrderAsync(buyerOrderId, ct) : null;
                try
                {
                    // A missed webhook must not pay out: both orders are read from their platform first, so a refund or
                    // chargeback only the platform knows of still holds the refund back or lowers what can be refunded.
                    if (buyerOrder is not null)
                        await RecoverAsync(buyerOrder, ct);
                    if (await repository.GetTicketAsync(offer.TicketId, ct) is { } old
                        && await repository.GetOrderAsync(old.OrderId, ct) is { } holderOrder)
                        await RecoverAsync(holderOrder, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Resale refund for offer {OfferId} waits: its orders could not be read from the platform", offer.Id);
                    waiting++;
                    continue;
                }
                if (buyerOrder is not { State: TicketCheckoutOrderState.Paid, RefundedMinor: 0, ChargedBackMinor: 0 }
                    || (await repository.GetRefundsAsync(buyerOrder.Id, ct)).Any(r => r.State != RefundState.Failed))
                {
                    if (await HoldBackResaleRefundAsync(offer, buyerOrder, ct))
                        heldBack++;
                    continue;
                }
                try
                {
                    if (await SettleResaleRefundAsync(offer, ct))
                    {
                        started++;
                        startedMinor += offer.RefundOwedMinor;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Resale refund for offer {OfferId} failed; the next batch retries", offer.Id);
                }
            }
            if (started + heldBack > 0)
                await auditLog.LogAsync(TicketsAuditActions.TicketResaleRefundBatch, TicketingSettingsAuditEntityType, ticketing.Id,
                    $"Evening resale refund batch: {started} refund(s) started, {FormatMinor(startedMinor)} in total; " +
                    $"{heldBack} held back, each with its own entry saying why; " +
                    $"{waiting} left for a later batch", JobName);
            batch = new(batch.Started + started, batch.StartedMinor + startedMinor, batch.HeldBack + heldBack, batch.Waiting + waiting);
        }
        return batch;
    }

    /// <summary>Held back for good: the ticketing team decides, and a later batch never pays it on its own.</summary>
    private async Task<bool> HoldBackResaleRefundAsync(TicketResaleOffer offer, TicketCheckoutOrder? buyerOrder, CancellationToken ct)
    {
        if (await repository.GetTicketAsync(offer.TicketId, ct) is not { } old)
            return false;
        if ((await repository.GetRefundsAsync(old.OrderId, ct)).Any(r => r.Id == offer.Id))
        {
            // Asked for before a crash kept the offer from recording it: that refund is already on its way.
            offer.RefundId = offer.Id;
            await repository.UpdateOfferAsync(offer, ct);
            return false;
        }
        var why = $"{RefundHeldBack}: the buyer's order {buyerOrder?.Reference ?? "unknown"} is " +
            (buyerOrder is null ? "gone" : $"{buyerOrder.State} with {FormatMinor(buyerOrder.RefundedMinor)} refunded and {FormatMinor(buyerOrder.ChargedBackMinor)} charged back");
        await RecordResaleRefundNotSentAsync(offer, old, why,
            $"{why}; the ticketing team decides on the {FormatMinor(offer.RefundOwedMinor)} owed for ticket {old.Barcode}", ct);
        return true;
    }

    /// <summary>Finishes what a crash left behind: paid orders without their tickets, resale refund or delivery, and refunds that never reached the platform.</summary>
    private async Task<int> RepairPaidAsync(CancellationToken ct)
    {
        var repaired = 0;
        foreach (var ticketing in await repository.ListTicketingSettingsAsync(ct))
        {
            var eventTickets = await repository.GetTicketsForSettingsAsync(ticketing.Id, ct);
            var issued = eventTickets.Select(t => t.OrderId).ToHashSet();
            var orders = await repository.GetOrdersForSettingsAsync(ticketing.Id, ct);
            // A sold offer whose old ticket or a child riding on it is still live: the resale stopped halfway.
            var unfinished = (await repository.GetOffersForSettingsAsync(ticketing.Id, ct))
                .Where(o => o.State == TicketResaleOfferState.Sold && eventTickets.Any(t =>
                    (t.Id == o.TicketId && t.State != TicketCheckoutTicketState.Resold)
                    || (t.ParentTicketId == o.TicketId && t.State == TicketCheckoutTicketState.Valid)))
                .Select(o => o.SoldToOrderId)
                .ToHashSet();
            foreach (var order in orders.Where(o => o.State == TicketCheckoutOrderState.Paid
                         && ((!issued.Contains(o.Id) && o.Lines.Any(l => l.Kind == OrderLineKind.Ticket)) || unfinished.Contains(o.Id))))
            {
                try
                {
                    await CompletePaidAsync(order, ct);
                    repaired++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Finishing paid order {OrderId} failed; next run retries", order.Id);
                }
            }

            // Tickets or a donation receipt never handed to delivery. Delivery dedupes a repeat;
            // the wait lets a first hand-off still in its queue finish, and the window keeps old orders out.
            var now = clock.GetCurrentInstant();
            foreach (var order in orders.Where(o => deliveries.Any() && o is { State: TicketCheckoutOrderState.Paid, PaidAt: { } paidAt }
                         && (issued.Contains(o.Id) || !o.Lines.Any(l => l.Kind == OrderLineKind.Ticket))
                         && paidAt <= now - DeliveryGrace && paidAt > now - DeliveryRepairWindow))
            {
                if ((await repository.GetOrderEventsAsync(order.Id, ct)).Any(e =>
                        e.Kind is TicketCheckoutOrderEventKind.TicketsEmailed or TicketCheckoutOrderEventKind.TicketMailSkipped))
                    continue;
                await HandOffToDeliveryAsync(order.Id, ct);
            }

            // A refund whose platform call threw may or may not exist there; repeating it under its id settles which.
            foreach (var unsent in (await repository.GetRefundsForSettingsAsync(ticketing.Id, ct))
                         .Where(r => r is { State: RefundState.Pending, PlatformRefundId: null }))
            {
                try
                {
                    await RefundCoreAsync(unsent.OrderId, unsent.AmountMinor, unsent.Reason, null, ct, unsent.Id);
                    if ((await repository.GetRefundsAsync(unsent.OrderId, ct)).FirstOrDefault(r => r.Id == unsent.Id)
                        is not { State: RefundState.Pending, PlatformRefundId: null })
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
    /// Repairs offers held by an order that will never pay or already paid, and tells each holder whose ticket is
    /// still in resale once sales have closed. Returns how many changed.
    /// </summary>
    private async Task<int> SweepOffersAsync(CancellationToken ct)
    {
        var changed = 0;
        var now = clock.GetCurrentInstant();
        foreach (var ticketing in await repository.ListTicketingSettingsAsync(ct))
        {
            // A cancelled event is closed too, but its holders are refunded by the cancel sweep, not told there is no refund.
            var closed = ticketing.CancelledAt is null && ticketing.IsClosed(now);
            foreach (var offer in await repository.GetOffersForSettingsAsync(ticketing.Id, ct))
            {
                if (ticketing.EffectiveSwitches.Resale == ResaleScheme.Off && offer.State == TicketResaleOfferState.Open)
                {
                    await WithdrawResaleOfferAsync(offer, null, ct);
                    changed++;
                    continue;
                }
                if (offer is { State: TicketResaleOfferState.Sold, SoldNotificationPending: true }
                    && (await repository.GetTicketAsync(offer.TicketId, ct))?.State == TicketCheckoutTicketState.Resold
                    && await TellDeliveryAsync(d => d.ResaleSoldAsync(offer.TicketId, offer.RefundOwedMinor, ct), offer.TicketId))
                    changed++;
                if (closed && offer is { State: TicketResaleOfferState.Open, UnsoldNotifiedAt: null }
                    && await TellDeliveryAsync(d => d.ResaleUnsoldAsync(offer.TicketId, ct), offer.TicketId))
                    changed++;
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
