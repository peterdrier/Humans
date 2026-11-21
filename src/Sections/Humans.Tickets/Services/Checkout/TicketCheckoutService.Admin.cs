using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Services.Checkout;

/// <summary>The ticketing team's exceptions on issued tickets: rename, void, free companion. All audited.</summary>
internal sealed partial class TicketCheckoutService
{
    internal const int MaxReasonLength = 500;

    public async Task<CheckoutMutationResult> RenameTicketAsync(
        Guid ticketId, string holderName, string holderEmail, string reason, Guid actorUserId, CancellationToken ct = default)
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
            if (ticket.ClassLabel is not null || ticket.CreditsMinor > 0)
                return new("Only a full price ticket can move to another person; this one was bought at a class price or with credits.");
            if ((await repository.GetTicketsForSalesEventAsync(ticket.SalesEventId, ct))
                .Any(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid))
                return new("Void this ticket's child and companion tickets before moving it to another person.");
            if (existing is not null && await HoldsTicketAsync(ticket.SalesEventId, existing.Id, clock.GetCurrentInstant(), ct))
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
            if (holderUserId != fromUserId && await HoldsTicketAsync(ticket.SalesEventId, holderUserId, clock.GetCurrentInstant(), ct))
                return new("That person already holds a ticket for this event.");
            samePerson = holderUserId == fromUserId;
        }

        ticket.HolderUserId = holderUserId;
        ticket.HolderName = holderName;
        ticket.HolderEmail = holderEmail;
        await repository.UpdateTicketAsync(ticket, ct);
        // The order line follows the ticket, so the new holder can open the order and the old one no longer can.
        var order = await repository.GetOrderAsync(ticket.OrderId, ct);
        var line = order?.Lines.FirstOrDefault(l =>
            l.Kind == CheckoutLineKind.Ticket && l.HolderKind is null or TicketHolderKind.Adult && l.AttendeeUserId == fromUserId);
        if (order is not null && line is not null)
        {
            line.AttendeeUserId = holderUserId;
            line.AttendeeName = holderName;
            line.AttendeeEmail = holderEmail;
            await repository.UpdateOrderAsync(order, ct);
        }

        var why = reason.Trim();
        var what = samePerson ? "name corrected" : "moved to another person";
        // Names stay out of the timeline (erasure leaves it); the audit rows link the people by id.
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.Renamed,
            $"Ticket {ticket.Barcode} {what} by the ticketing team: {why}", actorUserId, ct);
        var description = $"Ticket {ticket.Barcode} {what}" +
            (samePerson ? string.Empty : $" (from user {fromUserId?.ToString() ?? "none"} to user {holderUserId})") + $": {why}";
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutTicketRenamed, AuditEntityType, ticket.OrderId,
            description, actorUserId, holderUserId, AuditSubjectTypes.User);
        if (!samePerson && fromUserId is { } previous)
            await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutTicketRenamed, AuditEntityType, ticket.OrderId,
                description, actorUserId, previous, AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> VoidTicketAsync(
        Guid ticketId, string reason, Guid actorUserId, CancellationToken ct = default)
    {
        if (ReasonRefusal(reason) is { } refusal)
            return refusal;
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            return new("That ticket does not exist.");
        if (ticket.State is not (TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered))
            return new($"The ticket is already {ticket.State}.");
        if (ticket.ParentTicketId is { } parentId && (await repository.GetTicketAsync(parentId, ct))?.State == TicketCheckoutTicketState.Offered)
            return new("The adult's ticket is in the resale queue and its refund includes this one: withdraw it from the queue first.");

        var offer = (await repository.GetOffersForSalesEventAsync(ticket.SalesEventId, ct))
            .FirstOrDefault(o => o.TicketId == ticket.Id && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved);
        if (offer?.State == TicketResaleOfferState.Reserved)
            return new("A buyer's checkout is paying for this ticket right now; try again once it settles.");

        var now = clock.GetCurrentInstant();
        if (offer is not null)
        {
            offer.State = TicketResaleOfferState.Withdrawn;
            offer.WithdrawnAt = now;
            offer.DirectedTokenHash = null;
            await repository.UpdateOfferAsync(offer, ct);
        }

        var dependents = (await repository.GetTicketsForSalesEventAsync(ticket.SalesEventId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .ToList();
        var why = reason.Trim();
        foreach (var voided in dependents.Prepend(ticket))
        {
            voided.State = TicketCheckoutTicketState.Void;
            voided.VoidedAt = now;
            voided.VoidReason = voided == ticket ? why : "Voided with the adult's ticket";
            await repository.UpdateTicketAsync(voided, ct);
            await TimelineAsync(voided.OrderId, TicketCheckoutOrderEventKind.Voided,
                $"Ticket {voided.Barcode} voided by the ticketing team: {voided.VoidReason}" +
                (voided == ticket && offer is not null ? "; taken out of the resale queue" : string.Empty), actorUserId, ct);
        }
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} voided" + (dependents.Count > 0 ? $" with {dependents.Count} dependent ticket(s)" : string.Empty) +
            (offer is not null ? ", withdrawing its resale offer" : string.Empty) + $": {why}",
            actorUserId, ticket.HolderUserId, ticket.HolderUserId is null ? null : AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> IssueCompanionAsync(
        Guid parentTicketId, string holderName, string? holderEmail, string reason, Guid actorUserId, CancellationToken ct = default)
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
        }, reason.Trim(), actorUserId, ct)).Result;
    }

    /// <summary>
    /// Every check that needs no companion account runs before <paramref name="holder"/> is asked for one, so a refused
    /// issue leaves no account behind.
    /// </summary>
    private async Task<(CheckoutMutationResult Result, TicketCheckoutTicket? Companion)> IssueCompanionCoreAsync(
        Guid parentTicketId, string holderName, string? holderEmail,
        Func<CancellationToken, Task<(Guid? UserId, string? Refusal)>> holder, string why, Guid actorUserId, CancellationToken ct)
    {
        var parent = await repository.GetTicketAsync(parentTicketId, ct);
        if (parent is null)
            return (new("That ticket does not exist."), null);
        if (parent.State != TicketCheckoutTicketState.Valid || parent.Kind != TicketHolderKind.Adult || parent.HolderUserId is not { } parentHolderId)
            return (new("A companion attaches to a valid adult ticket with a holder."), null);
        var salesEvent = await repository.GetSalesEventAsync(parent.SalesEventId, ct);
        if (salesEvent is null)
            return (new("That ticket's sales event no longer exists."), null);
        var now = clock.GetCurrentInstant();
        var eventTickets = await repository.GetTicketsForSalesEventAsync(parent.SalesEventId, ct);
        if (eventTickets.Any(t => t.ParentTicketId == parent.Id && t.Kind == TicketHolderKind.Companion && t.State == TicketCheckoutTicketState.Valid))
            return (new("This ticket already has a companion."), null);
        if ((await LoadStockAsync(salesEvent, now, ct)).Held >= salesEvent.Capacity)
            return (new("The event is at capacity."), null);

        var (holderUserId, holderRefusal) = await holder(ct);
        if (holderRefusal is not null)
            return (new(holderRefusal), null);
        if (holderUserId is { } companionId && (companionId == parentHolderId || eventTickets.Any(t =>
                t.HolderUserId == companionId && t.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered)))
            return (new("That person already holds a ticket for this event."), null);
        var parentOrder = await repository.GetOrderAsync(parent.OrderId, ct);
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            SalesEventId = salesEvent.Id,
            BuyerUserId = parentHolderId,
            BuyerName = parent.HolderName,
            BuyerEmail = parent.HolderEmail ?? parentOrder?.BuyerEmail ?? string.Empty,
            BackendKey = salesEvent.BackendKey,
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
                    Kind = CheckoutLineKind.Ticket,
                    HolderKind = TicketHolderKind.Companion,
                    Description = "Companion ticket",
                    AttendeeName = holderName,
                    AttendeeEmail = holderEmail,
                    AttendeeUserId = holderUserId,
                    WaveName = parent.WaveName,
                },
            ],
        };
        await repository.AddOrderAsync(order, ct);
        var companion = new TicketCheckoutTicket
        {
            Id = Guid.NewGuid(),
            SalesEventId = salesEvent.Id,
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

        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.CompanionIssued,
            $"Free companion ticket {companion.Barcode} for ticket {parent.Barcode}: {why}", actorUserId, ct);
        await TimelineAsync(parent.OrderId, TicketCheckoutOrderEventKind.CompanionIssued,
            $"Free companion ticket {companion.Barcode} attached to ticket {parent.Barcode}", actorUserId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCompanionIssued, AuditEntityType, order.Id,
            $"Companion ticket {companion.Barcode} issued for ticket {parent.Barcode}: {why}",
            actorUserId, parentHolderId, AuditSubjectTypes.User);
        await HandOffToDeliveryAsync(order.Id, ct);
        return (CheckoutMutationResult.Ok, companion);
    }

    private static CheckoutMutationResult? ReasonRefusal(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? new("Give a reason; it goes on the order's timeline and the audit log.")
        : reason.Trim().Length > MaxReasonLength ? new($"Keep the reason under {MaxReasonLength} characters.")
        : null;
}
