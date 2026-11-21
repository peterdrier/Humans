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
            return new("A rename needs the new holder's legal name and email.");
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            return new("That ticket does not exist.");
        if (ticket.State != TicketCheckoutTicketState.Valid || ticket.Kind != TicketHolderKind.Adult)
            return new("Only a valid adult ticket can be renamed.");
        if (ticket.ClassLabel is not null || ticket.CreditsMinor > 0)
            return new("Only a full price ticket can be renamed; this one was bought at a class price or with credits.");
        if ((await repository.GetTicketsForSalesEventAsync(ticket.SalesEventId, ct))
            .Any(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid))
            return new("Void or move this ticket's child and companion tickets first.");

        var holder = await provisioning.FindOrCreateUserByEmailAsync(holderEmail, holderName, ContactSource.Manual, ct);
        if (holder.UserId == ticket.HolderUserId)
            return new("That person already holds this ticket.");
        if (await HoldsTicketAsync(ticket.SalesEventId, holder.UserId, clock.GetCurrentInstant(), ct))
            return new("That person already holds a ticket for this event.");

        var fromUserId = ticket.HolderUserId;
        ticket.HolderUserId = holder.UserId;
        ticket.HolderName = holderName;
        ticket.HolderEmail = holderEmail;
        await repository.UpdateTicketAsync(ticket, ct);
        // Names stay out of the timeline (erasure leaves it); the audit entry links both people by id.
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.Renamed,
            $"Ticket {ticket.Barcode} renamed by the ticketing team: {Truncate(reason.Trim())}", actorUserId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutTicketRenamed, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} moved from user {fromUserId?.ToString() ?? "none"} to user {holder.UserId}: {Truncate(reason.Trim())}",
            actorUserId);
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
        var why = Truncate(reason.Trim());
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
            $": {why}", actorUserId);
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
        var parent = await repository.GetTicketAsync(parentTicketId, ct);
        if (parent is null)
            return new("That ticket does not exist.");
        if (parent.State != TicketCheckoutTicketState.Valid || parent.Kind != TicketHolderKind.Adult)
            return new("A companion attaches to a valid adult ticket.");
        var salesEvent = await repository.GetSalesEventAsync(parent.SalesEventId, ct);
        if (salesEvent is null)
            return new("That ticket's sales event no longer exists.");
        var now = clock.GetCurrentInstant();
        if ((await repository.GetTicketsForSalesEventAsync(parent.SalesEventId, ct))
            .Any(t => t.ParentTicketId == parent.Id && t.Kind == TicketHolderKind.Companion && t.State == TicketCheckoutTicketState.Valid))
            return new("This ticket already has a companion.");
        if ((await LoadStockAsync(salesEvent, now, ct)).Held >= salesEvent.Capacity)
            return new("The event is at capacity.");

        Guid? holderUserId = holderEmail is null
            ? null
            : (await provisioning.FindOrCreateUserByEmailAsync(holderEmail, holderName, ContactSource.Manual, ct)).UserId;
        var parentOrder = await repository.GetOrderAsync(parent.OrderId, ct);
        var order = new TicketCheckoutOrder
        {
            Id = Guid.NewGuid(),
            SalesEventId = salesEvent.Id,
            BuyerUserId = parent.HolderUserId ?? actorUserId,
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

        var why = Truncate(reason.Trim());
        await TimelineAsync(order.Id, TicketCheckoutOrderEventKind.CompanionIssued,
            $"Free companion ticket {companion.Barcode} for ticket {parent.Barcode}: {why}", actorUserId, ct);
        await TimelineAsync(parent.OrderId, TicketCheckoutOrderEventKind.CompanionIssued,
            $"Free companion ticket {companion.Barcode} attached to ticket {parent.Barcode}", actorUserId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCompanionIssued, AuditEntityType, order.Id,
            $"Companion ticket {companion.Barcode} issued for ticket {parent.Barcode}: {why}", actorUserId);
        await DeliverAsync(order.Id, ct);
        return CheckoutMutationResult.Ok;
    }

    private static CheckoutMutationResult? ReasonRefusal(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? new("Give a reason; it goes on the order's timeline and the audit log.") : null;
}
