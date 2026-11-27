using Humans.AuditLog.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Services.Checkout;

/// <summary>The ticketing team's exceptions on issued tickets: rename, void and refund, free companion, and the overrides. All audited.</summary>
internal sealed partial class TicketCheckoutService
{
    internal const int MaxReasonLength = 500;

    public async Task<CheckoutMutationResult> RenameTicketAsync(
        Guid ticketId, string holderName, string holderEmail, string reason, bool overrideRules, Guid actorUserId, CancellationToken ct = default)
    {
        holderName = holderName.Trim();
        holderEmail = holderEmail.Trim();
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        if (holderName.Length == 0 || !LooksLikeEmail(holderEmail))
            return new("A rename needs the holder's legal name and email.");
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            return new("That ticket does not exist.");
        if (ticket.State != TicketCheckoutTicketState.Valid || ticket.Kind != TicketHolderKind.Adult)
            return new("Only a valid adult ticket can be renamed.");

        var fromUserId = ticket.HolderUserId;
        var existing = await users.GetByEmailOrAlternateAsync(holderEmail, ct);
        var samePerson = existing is not null && existing.Id == fromUserId;
        // Correcting the same person's legal name is always allowed; moving the ticket to someone else is the exception.
        if (!samePerson)
        {
            if (!overrideRules && (ticket.ClassLabel is not null || ticket.DiscountsMinor > 0))
                return new("Only a full price ticket can move to another person; this one was bought at a class price or with discounts.");
            if ((await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
                .Any(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid))
                return new("Void this ticket's child and companion tickets before moving it to another person.");
            if (existing is not null && await HoldsTicketAsync(ticket.TicketingSettingsId, existing.Id, clock.GetCurrentInstant(), ct))
                return new("That person already holds a ticket for this event.");
        }
        // Only once the rename is allowed, so a refused move leaves no account behind.
        var holderUserId = existing?.Id ?? Guid.Empty;
        if (existing is null)
        {
            var (resolved, problem) = await ResolveAccountAsync(holderEmail, ct);
            if (resolved is not { } resolvedId)
                return new($"That email cannot take the ticket: {problem}.");
            holderUserId = resolvedId;
            // The resolver also matches aliases, which the lookup above may not.
            if (holderUserId != fromUserId && await HoldsTicketAsync(ticket.TicketingSettingsId, holderUserId, clock.GetCurrentInstant(), ct))
                return new("That person already holds a ticket for this event.");
            samePerson = holderUserId == fromUserId;
        }

        ticket.HolderUserId = holderUserId;
        ticket.HolderName = holderName;
        ticket.HolderEmail = holderEmail;
        await repository.UpdateTicketAsync(ticket, ct);
        holdings.InvalidateHoldings(holderUserId);
        // The order line follows the ticket, so the new holder can open the order and the old one no longer can.
        var order = await repository.GetOrderAsync(ticket.OrderId, ct);
        var line = order?.Lines.FirstOrDefault(l =>
            l.Kind == OrderLineKind.Ticket && l.HolderKind is null or TicketHolderKind.Adult && l.HolderUserId == fromUserId);
        if (order is not null && line is not null)
        {
            line.HolderUserId = holderUserId;
            line.HolderName = holderName;
            line.HolderEmail = holderEmail;
            await repository.UpdateOrderAsync(order, ct);
        }

        var why = reason.Trim();
        // Only a move of a reduced price ticket steps past a rule; anything else is a plain rename.
        var overridden = overrideRules && !samePerson && (ticket.ClassLabel is not null || ticket.DiscountsMinor > 0);
        var what = (samePerson ? "name corrected" : "moved to another person") + (overridden ? " by an override" : string.Empty);
        // Names stay out of the timeline (erasure leaves it); the audit rows link the people by id.
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.Renamed,
            $"Ticket {ticket.Barcode} {what} by the ticketing team: {why}", actorUserId, ct);
        var description = (overridden ? "Override: " : string.Empty) + $"Ticket {ticket.Barcode} {what}" +
            (samePerson ? string.Empty : $" (from user {fromUserId?.ToString() ?? "none"} to user {holderUserId})") + $": {why}";
        var action = overridden ? TicketsAuditActions.TicketOverride : TicketsAuditActions.TicketCheckoutTicketRenamed;
        await auditLog.LogAsync(action, AuditEntityType, ticket.OrderId,
            description, actorUserId, holderUserId, AuditSubjectTypes.User);
        if (!samePerson && fromUserId is { } previous)
            await auditLog.LogAsync(action, AuditEntityType, ticket.OrderId,
                description, actorUserId, previous, AuditSubjectTypes.User);
        await TellDeliveryAsync(d => d.TicketRenamedAsync(ticket.Id, ct), ticket.Id);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> VoidTicketAsync(
        Guid ticketId, string reason, long refundMinor, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        if (refundMinor < 0)
            return new("The refund cannot be negative.");
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            return new("That ticket does not exist.");
        if (!ticket.IsHeld)
            return new($"The ticket is already {ticket.State}.");
        if (ticket.ParentTicketId is { } parentId && (await repository.GetTicketAsync(parentId, ct))?.State == TicketCheckoutTicketState.Offered)
            return new("The adult's ticket is in the resale queue and its refund includes this one: withdraw it from the queue first.");

        var offer = (await repository.GetOffersForSettingsAsync(ticket.TicketingSettingsId, ct))
            .FirstOrDefault(o => o.TicketId == ticket.Id && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved);
        if (offer?.State == TicketResaleOfferState.Reserved)
            return new("A buyer's order is paying for this ticket right now; try again once it settles.");

        var why = reason.Trim();
        // Refund first: a refund the platform turns down leaves the ticket working, so the team can try again.
        if (refundMinor > 0)
        {
            var (refunded, _) = await RefundCoreAsync(ticket.OrderId, refundMinor, $"Ticket {ticket.Barcode} voided: {why}", actorUserId, ct);
            if (!refunded.Succeeded)
                return new($"{refunded.Refusal} Nothing was voided.");
        }

        var dependents = await VoidWithDependentsAsync(ticket, offer, why, actorUserId,
            refundMinor > 0 ? $"; {FormatMinor(refundMinor)} refunded" : "; no refund: no money goes back", ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} voided" + (dependents > 0 ? $" with {dependents} dependent ticket(s)" : string.Empty) +
            (offer is not null ? ", withdrawing its resale offer" : string.Empty) +
            (refundMinor > 0 ? $", refunding {FormatMinor(refundMinor)}" : ", with no refund") + $": {why}",
            actorUserId, ticket.HolderUserId, ticket.HolderUserId is null ? null : AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    /// <summary>Voids the ticket and its valid dependents and withdraws <paramref name="offer"/>; returns how many dependents went with it.</summary>
    private async Task<int> VoidWithDependentsAsync(
        TicketCheckoutTicket ticket, TicketResaleOffer? offer, string why, Guid? actorUserId, string note, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        if (offer is not null)
        {
            offer.State = TicketResaleOfferState.Withdrawn;
            offer.WithdrawnAt = now;
            offer.Invites = [];
            await repository.UpdateOfferAsync(offer, ct);
        }

        var dependents = (await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .ToList();
        foreach (var voided in dependents.Prepend(ticket))
        {
            voided.State = TicketCheckoutTicketState.Void;
            voided.VoidedAt = now;
            voided.VoidReason = voided == ticket ? why : "Voided with the adult's ticket";
            await repository.UpdateTicketAsync(voided, ct);
            await TimelineAsync(voided.OrderId, TicketCheckoutOrderEventKind.Voided,
                $"Ticket {voided.Barcode} voided: {voided.VoidReason}" +
                (voided == ticket ? note + (offer is not null ? "; taken out of the resale queue" : string.Empty) : string.Empty),
                actorUserId, ct);
        }
        return dependents.Count;
    }

    /// <summary>The override for a ticket voided by mistake: it works again, whatever was refunded for it.</summary>
    public async Task<CheckoutMutationResult> RestoreTicketAsync(
        Guid ticketId, string reason, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            return new("That ticket does not exist.");
        if (ticket.State != TicketCheckoutTicketState.Void)
            return new("Only a voided ticket can be restored.");
        if ((await repository.GetTicketingSettingsAsync(ticket.TicketingSettingsId, ct))?.CancelledAt is not null)
            return new("The event is cancelled; its tickets stay void.");
        if (ticket.ParentTicketId is { } parentId && (await repository.GetTicketAsync(parentId, ct))?.State != TicketCheckoutTicketState.Valid)
            return new("Restore the adult's ticket first.");
        if (ticket.HolderUserId is { } holder && await HoldsTicketAsync(ticket.TicketingSettingsId, holder, clock.GetCurrentInstant(), ct))
            return new("The holder already holds another ticket for this event.");

        var why = reason.Trim();
        var wasVoided = ticket.VoidReason;
        ticket.State = TicketCheckoutTicketState.Valid;
        ticket.VoidedAt = null;
        ticket.VoidReason = null;
        await repository.UpdateTicketAsync(ticket, ct);
        if (ticket.HolderUserId is { } holderId)
            holdings.InvalidateHoldings(holderId);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.Restored,
            $"Ticket {ticket.Barcode} restored by an override: {why} (it was voided: {wasVoided})", actorUserId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketOverride, AuditEntityType, ticket.OrderId,
            $"Override: ticket {ticket.Barcode} restored after it was voided ({wasVoided}): {why}",
            actorUserId, ticket.HolderUserId, ticket.HolderUserId is null ? null : AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> IssueCompanionAsync(
        Guid parentTicketId, string holderName, string? holderEmail, string reason, bool overrideCapacity, Guid actorUserId, CancellationToken ct = default)
    {
        holderName = holderName.Trim();
        holderEmail = string.IsNullOrWhiteSpace(holderEmail) ? null : holderEmail.Trim();
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        if (holderName.Length == 0 || (holderEmail is not null && !LooksLikeEmail(holderEmail)))
            return new("A companion needs a legal name; the email is optional but must be valid.");
        return (await IssueCompanionCoreAsync(parentTicketId, holderName, holderEmail, async c =>
        {
            if (holderEmail is null)
                return (null, null);
            var (resolved, problem) = await ResolveAccountAsync(holderEmail, c);
            return resolved is null
                ? (null, $"That email cannot take the ticket: {problem}. Leave it empty to issue the companion without an account.")
                : (resolved, null);
        }, reason.Trim(), overrideCapacity, actorUserId, ct)).Result;
    }

    /// <summary>
    /// Every check that needs no companion account runs before <paramref name="holder"/> is asked for one, so a refused
    /// issue leaves no account behind.
    /// </summary>
    private async Task<(CheckoutMutationResult Result, TicketCheckoutTicket? Companion)> IssueCompanionCoreAsync(
        Guid parentTicketId, string holderName, string? holderEmail,
        Func<CancellationToken, Task<(Guid? UserId, string? Refusal)>> holder, string why, bool overrideCapacity, Guid actorUserId,
        CancellationToken ct)
    {
        var parent = await repository.GetTicketAsync(parentTicketId, ct);
        if (parent is null)
            return (new("That ticket does not exist."), null);
        if (parent.State != TicketCheckoutTicketState.Valid || parent.Kind != TicketHolderKind.Adult || parent.HolderUserId is not { } parentHolderId)
            return (new("A companion attaches to a valid adult ticket with a holder."), null);
        var ticketing = await repository.GetTicketingSettingsAsync(parent.TicketingSettingsId, ct);
        if (ticketing is null)
            return (new("That ticket's ticketing settings no longer exist."), null);
        var now = clock.GetCurrentInstant();
        var eventTickets = await repository.GetTicketsForSettingsAsync(parent.TicketingSettingsId, ct);
        if (eventTickets.Any(t => t.ParentTicketId == parent.Id && t.Kind == TicketHolderKind.Companion && t.State == TicketCheckoutTicketState.Valid))
            return (new("This ticket already has a companion."), null);
        if (ticketing.CancelledAt is not null)
            return (new("The event is cancelled."), null);
        var overCapacity = (await LoadStockAsync(ticketing, now, ct)).Held >= ticketing.Capacity;
        if (overCapacity && !overrideCapacity)
            return (new("The event is at capacity."), null);

        var (holderUserId, holderRefusal) = await holder(ct);
        if (holderRefusal is not null)
            return (new(holderRefusal), null);
        if (holderUserId is { } companionId && (companionId == parentHolderId || eventTickets.Any(t =>
                t.HolderUserId == companionId && t.IsHeld)))
            return (new("That person already holds a ticket for this event."), null);
        var parentOrder = await repository.GetOrderAsync(parent.OrderId, ct);
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticketing.Id,
            BuyerUserId = parentHolderId,
            BuyerName = parent.HolderName,
            BuyerEmail = parent.HolderEmail ?? parentOrder?.BuyerEmail ?? string.Empty,
            PlatformKey = ticketing.PlatformKey,
            State = TicketCheckoutOrderState.Paid,
            Culture = parentOrder?.Culture ?? "en",
            TotalMinor = 0,
            PaidAt = now,
            CreatedAt = now,
            ExpiresAt = now,
            UpdatedAt = now,
            Lines =
            [
                new TicketCheckoutOrderLine
                {
                    Kind = OrderLineKind.Ticket,
                    HolderKind = TicketHolderKind.Companion,
                    Description = "Companion ticket",
                    HolderName = holderName,
                    HolderEmail = holderEmail,
                    HolderUserId = holderUserId,
                    WaveName = parent.WaveName,
                },
            ],
        };
        await repository.AddOrderAsync(order, ct);
        var companion = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            TicketingSettingsId = ticketing.Id,
            OrderId = order.Id,
            Kind = TicketHolderKind.Companion,
            ParentTicketId = parent.Id,
            HolderUserId = holderUserId,
            HolderName = holderName,
            HolderEmail = holderEmail,
            Barcode = NewBarcode(),
            WaveName = parent.WaveName,
            State = TicketCheckoutTicketState.Valid,
            IssuedAt = now,
        };
        await repository.AddTicketsAsync([companion], ct);
        if (holderUserId is { } companionHolder)
            holdings.InvalidateHoldings(companionHolder);

        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.CompanionIssued,
            $"Free companion ticket {companion.Barcode} for ticket {parent.Barcode}: {why}", actorUserId, ct);
        await TimelineAsync(parent.OrderId, TicketCheckoutOrderEventKind.CompanionIssued,
            $"Free companion ticket {companion.Barcode} attached to ticket {parent.Barcode}", actorUserId, ct);
        var overridden = overCapacity && overrideCapacity;
        await auditLog.LogAsync(overridden ? TicketsAuditActions.TicketOverride : TicketsAuditActions.TicketCompanionIssued, AuditEntityType, order.Id,
            (overridden ? "Override: past capacity, c" : "C") + $"ompanion ticket {companion.Barcode} issued for ticket {parent.Barcode}: {why}",
            actorUserId, parentHolderId, AuditSubjectTypes.User);
        await HandOffToDeliveryAsync(order.Id, ct);
        return (CheckoutMutationResult.Ok, companion);
    }

    private static CheckoutMutationResult? ReasonRefusal(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? new("Give a reason; it goes on the order's timeline and the audit log.")
        : reason.Trim().Length > MaxReasonLength ? new($"Keep the reason under {MaxReasonLength} characters.")
        : null;
}
