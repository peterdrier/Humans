using Humans.AuditLog.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
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
        if (holderName.Length is 0 or > MaxNameLength || !LooksLikeEmail(holderEmail))
            return new($"A rename needs the holder's legal name of at most {MaxNameLength} characters and a valid email.");
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null)
            return new("That ticket does not exist.");
        if (ticket.VoidRefundId is not null)
            return new("Complete the pending void before renaming this ticket.");
        if (ticket.State != TicketCheckoutTicketState.Valid || ticket.Kind != TicketHolderKind.Adult)
            return new("Only a valid adult ticket can be renamed.");
        if (TicketPiiTombstone.IsTombstoned(ticket.HolderName, ticket.HolderEmail))
            return new("Erased ticket data cannot be renamed.");

        var order = await repository.GetOrderAsync(ticket.OrderId, ct);
        var line = order?.Lines.ElementAtOrDefault(ticket.LineIndex);
        if (line is null || line.Kind != OrderLineKind.Ticket || line.HolderKind is not (null or TicketHolderKind.Adult))
            return new("The ticket's adult order line is missing; nothing was renamed.");

        var fromUserId = ticket.HolderUserId;
        var (knownHolder, accountProblem) = await ResolveAccountAsync(holderEmail, ct, provision: false);
        if (accountProblem is not null)
            return new($"That email cannot take the ticket: {accountProblem}.");
        var samePerson = knownHolder is not null && knownHolder == fromUserId;
        // Correcting the same person's legal name is always allowed; moving the ticket to someone else is the exception.
        if (!samePerson)
        {
            if (!overrideRules && (ticket.ClassLabel is not null || ticket.DiscountsMinor > 0))
                return new("Only a full price ticket can move to another person; this one was bought at a class price or with discounts.");
            if ((await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
                .Any(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid))
                return new("Void this ticket's child and companion tickets before moving it to another person.");
            if (knownHolder is { } knownId && await HoldsTicketAsync(ticket.TicketingSettingsId, knownId, clock.GetCurrentInstant(), ct))
                return new("That person already holds a ticket for this event.");
        }
        // Only once the rename is allowed, so a refused move leaves no account behind.
        var holderUserId = knownHolder ?? Guid.Empty;
        if (knownHolder is null)
        {
            var (resolved, problem) = await ResolveAccountAsync(holderEmail, ct);
            if (resolved is not { } resolvedId)
                return new($"That email cannot take the ticket: {problem}.");
            holderUserId = resolvedId;
            if (holderUserId != fromUserId && await HoldsTicketAsync(ticket.TicketingSettingsId, holderUserId, clock.GetCurrentInstant(), ct))
                return new("That person already holds a ticket for this event.");
            samePerson = holderUserId == fromUserId;
        }

        ticket.HolderUserId = holderUserId;
        ticket.HolderName = holderName;
        ticket.HolderEmail = holderEmail;
        line.HolderUserId = holderUserId;
        line.HolderName = holderName;
        line.HolderEmail = holderEmail;
        await repository.UpdateOrderAsync(order!, ct, ticket);
        holdings.InvalidateHoldings(holderUserId);

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
        if (ticket.ParentTicketId is { } parentId && await repository.GetTicketAsync(parentId, ct) is { } parent)
        {
            if (parent.State == TicketCheckoutTicketState.Offered)
                return new("The adult's ticket is in the resale queue and its refund includes this one: withdraw it from the queue first.");
            if (parent.VoidRefundId is not null)
                return new("Complete the pending void of the adult ticket first.");
        }
        if ((await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
            .Any(t => t.ParentTicketId == ticket.Id && t.VoidRefundId is not null))
            return new("Complete the pending void of the dependent ticket first.");

        var offer = (await repository.GetOffersForSettingsAsync(ticket.TicketingSettingsId, ct))
            .FirstOrDefault(o => o.TicketId == ticket.Id && o.State is TicketResaleOfferState.Open or TicketResaleOfferState.Reserved);
        if (offer?.State == TicketResaleOfferState.Reserved)
            return new("A buyer's order is paying for this ticket right now; try again once it settles.");

        var why = reason.Trim();
        if (ticket.VoidRefundId is not null)
        {
            if (ticket.VoidRefundMinor is not { } originalAmount || ticket.VoidRequestedReason is not { } originalReason)
                return new("The pending void has incomplete details; the ticketing team must review it.");
            refundMinor = originalAmount;
            why = originalReason;
        }
        // The intent survives a stop after the refund, so a retry cannot request that money twice.
        if (refundMinor > 0)
        {
            if (ticket.VoidRefundId is null)
            {
                ticket.VoidRefundId = Guid.NewGuid();
                ticket.VoidRefundMinor = refundMinor;
                ticket.VoidRequestedReason = why;
                await repository.UpdateTicketAsync(ticket, ct);
                await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutVoidRequested, AuditEntityType, ticket.OrderId,
                    $"Void requested for ticket {ticket.Barcode}, refund {FormatMinor(refundMinor)}: {why}", actorUserId);
            }
            var (refunded, _) = await RefundCoreAsync(ticket.OrderId, refundMinor,
                Truncate($"Ticket {ticket.Barcode} voided: {why}"), actorUserId, ct, ticket.VoidRefundId);
            if (!refunded.Succeeded)
            {
                var saved = (await repository.GetRefundsAsync(ticket.OrderId, ct)).FirstOrDefault(r => r.Id == ticket.VoidRefundId);
                if (saved is null || saved.State == RefundState.Failed)
                {
                    ClearVoidIntent(ticket);
                    await repository.UpdateTicketAsync(ticket, ct);
                }
                return new($"{refunded.Refusal} Nothing was voided.");
            }
        }

        var dependents = await VoidWithDependentsAsync(ticket, offer, why, actorUserId,
            refundMinor > 0 ? $"; refund of {FormatMinor(refundMinor)} requested" : "; no refund: no money goes back", ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCheckoutTicketVoided, AuditEntityType, ticket.OrderId,
            $"Ticket {ticket.Barcode} voided" + (dependents > 0 ? $" with {dependents} dependent ticket(s)" : string.Empty) +
            (offer is not null ? ", withdrawing its resale offer" : string.Empty) +
            (refundMinor > 0 ? $", refund of {FormatMinor(refundMinor)} requested" : ", with no refund") + $": {why}",
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
        }

        var dependents = (await repository.GetTicketsForSettingsAsync(ticket.TicketingSettingsId, ct))
            .Where(t => t.ParentTicketId == ticket.Id && t.State == TicketCheckoutTicketState.Valid)
            .ToList();
        foreach (var voided in dependents.Prepend(ticket))
        {
            voided.State = TicketCheckoutTicketState.Void;
            voided.VoidedAt = now;
            voided.VoidReason = voided == ticket ? why : "Voided with the adult's ticket";
        }
        ClearVoidIntent(ticket);
        await repository.UpdateTicketAsync(ticket, ct, dependents, offer);
        foreach (var voided in dependents.Prepend(ticket))
        {
            await TimelineAsync(voided.OrderId, TicketCheckoutOrderEventKind.Voided,
                $"Ticket {voided.Barcode} voided: {voided.VoidReason}" +
                (voided == ticket ? note + (offer is not null ? "; taken out of the resale queue" : string.Empty) : string.Empty),
                actorUserId, ct);
        }
        return dependents.Count;
    }

    private static void ClearVoidIntent(TicketCheckoutTicket ticket)
    {
        ticket.VoidRefundId = null;
        ticket.VoidRefundMinor = null;
        ticket.VoidRequestedReason = null;
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
        if (TicketPiiTombstone.IsTombstoned(ticket.HolderName, ticket.HolderEmail))
            return new("Erased ticket data cannot be restored.");
        if ((await repository.GetTicketingSettingsAsync(ticket.TicketingSettingsId, ct))?.CancelledAt is not null)
            return new("The event is cancelled; its tickets stay void.");
        if (ticket.ParentTicketId is { } parentId)
        {
            var parent = await repository.GetTicketAsync(parentId, ct);
            if (parent?.State != TicketCheckoutTicketState.Valid)
                return new("Restore the adult's ticket first.");
            if (TicketPiiTombstone.IsTombstoned(parent.HolderName, parent.HolderEmail))
                return new("Erased adult ticket data cannot have dependents restored.");
        }
        if (ticket.HolderUserId is { } holder)
        {
            if (await users.GetUserInfoAsync(holder, ct) is not { IsTombstone: false })
                return new("The holder's account is no longer available; this ticket cannot be restored.");
            if (await HoldsTicketAsync(ticket.TicketingSettingsId, holder, clock.GetCurrentInstant(), ct))
                return new("The holder already holds another ticket for this event.");
        }

        var why = reason.Trim();
        var wasVoided = ticket.VoidReason;
        ticket.State = TicketCheckoutTicketState.Valid;
        ticket.VoidedAt = null;
        ticket.VoidReason = null;
        ClearVoidIntent(ticket);
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
        if (holderName.Length is 0 or > MaxNameLength || (holderEmail is not null && !LooksLikeEmail(holderEmail)))
            return new($"A companion needs a legal name of at most {MaxNameLength} characters; the email is optional but must be valid.");
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
        if (TicketPiiTombstone.IsTombstoned(parent.HolderName, parent.HolderEmail))
            return (new("Erased adult ticket data cannot receive a companion."), null);
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
                t.HolderUserId == companionId && t.IsHeld)
                || await HoldsTicketAsync(ticketing.Id, companionId, clock.GetCurrentInstant(), ct)))
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
        await repository.AddOrderAsync(order, ct, ticket: companion);
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
