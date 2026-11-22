using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Services.Checkout;

/// <summary>
/// Companion requests (B8): the holder asks, the ticketing team approves or declines. The holder's note never goes on
/// the timeline or the audit log, which many people read; it stays on the request.
/// </summary>
internal sealed partial class TicketCheckoutService
{
    private const int MaxCompanionNoteLength = 500;

    public async Task<CheckoutMutationResult> RequestCompanionAsync(
        Guid ticketId, Guid holderUserId, string companionEmail, string note, CancellationToken ct = default)
    {
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null || ticket.HolderUserId != holderUserId)
            return new("Tickets_Resale_NotFound");
        var salesEvent = await repository.GetSalesEventAsync(ticket.SalesEventId, ct);
        var requests = await repository.GetCompanionRequestsForSalesEventAsync(ticket.SalesEventId, ct);
        if (!CanAskCompanion(ticket, salesEvent, await repository.GetTicketsForSalesEventAsync(ticket.SalesEventId, ct), requests))
            return new("Tickets_Companion_NotAllowed");
        note = note.Trim();
        if (note.Length == 0 || note.Length > MaxCompanionNoteLength)
            return new("Tickets_Companion_NoteRequired");
        var now = clock.GetCurrentInstant();
        var (companion, problem) = await FindOtherAsync(companionEmail.Trim(), holderUserId, ticket.SalesEventId, now, ct);
        if (problem is not null)
            return new("Tickets_Companion_" + problem);

        var request = new TicketCompanionRequest
        {
            Id = Guid.NewGuid(),
            SalesEventId = ticket.SalesEventId,
            TicketId = ticket.Id,
            HolderUserId = holderUserId,
            CompanionUserId = companion!.Id,
            Note = note,
            State = TicketCompanionRequestState.Pending,
            RequestedAt = now,
        };
        await repository.AddCompanionRequestAsync(request, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.CompanionRequested,
            $"Holder asked for a companion on ticket {ticket.Barcode}; waiting for the ticketing team", holderUserId, ct);
        return CheckoutMutationResult.Ok;
    }

    public async Task<CheckoutMutationResult> WithdrawCompanionRequestAsync(Guid ticketId, Guid holderUserId, CancellationToken ct = default)
    {
        var ticket = await repository.GetTicketAsync(ticketId, ct);
        if (ticket is null || ticket.HolderUserId != holderUserId)
            return new("Tickets_Resale_NotFound");
        var pending = (await repository.GetCompanionRequestsForSalesEventAsync(ticket.SalesEventId, ct))
            .FirstOrDefault(r => r.TicketId == ticketId && r.State == TicketCompanionRequestState.Pending);
        if (pending is null)
            return new("Tickets_Companion_NothingPending");
        pending.State = TicketCompanionRequestState.Withdrawn;
        pending.DecidedAt = clock.GetCurrentInstant();
        await repository.UpdateCompanionRequestAsync(pending, ct);
        await TimelineAsync(ticket.OrderId, TicketCheckoutOrderEventKind.CompanionRequestDecided,
            $"Holder withdrew the companion request on ticket {ticket.Barcode}", holderUserId, ct);
        return CheckoutMutationResult.Ok;
    }

    public async Task<IReadOnlyList<CompanionRequestRow>> GetCompanionRequestsAsync(Guid salesEventId, CancellationToken ct = default)
    {
        var tickets = (await repository.GetTicketsForSalesEventAsync(salesEventId, ct)).ToDictionary(t => t.Id);
        var rows = new List<CompanionRequestRow>();
        foreach (var request in await repository.GetCompanionRequestsForSalesEventAsync(salesEventId, ct))
        {
            tickets.TryGetValue(request.TicketId, out var ticket);
            var companion = await users.GetUserInfoAsync(request.CompanionUserId, ct);
            rows.Add(new CompanionRequestRow(request, ticket?.HolderName ?? string.Empty,
                companion?.FullName ?? string.Empty, ticket?.Barcode ?? string.Empty, ticket?.OrderId ?? Guid.Empty));
        }
        return rows;
    }

    public async Task<CheckoutMutationResult> DecideCompanionRequestAsync(
        Guid requestId, bool approve, string? note, Guid actorUserId, CancellationToken ct = default)
    {
        var request = await repository.GetCompanionRequestAsync(requestId, ct);
        if (request is null)
            return new("That request does not exist.");
        if (request.State != TicketCompanionRequestState.Pending)
            return new("That request was already answered or withdrawn.");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > MaxCompanionNoteLength)
            return new($"Keep the note under {MaxCompanionNoteLength} characters.");
        if (!approve && note is null)
            return new("Say why it is declined; the holder sees it.");

        TicketCheckoutTicket? companionTicket = null;
        if (approve)
        {
            var (companion, problem) = await CheckOtherAsync(
                request.CompanionUserId, request.HolderUserId, request.SalesEventId, clock.GetCurrentInstant(), ct);
            if (problem is not null)
                return new(problem switch
                {
                    "Holds" => "The companion now holds a ticket of their own; decline the request.",
                    "NeedsName" => "The companion has no legal name in Humans yet; ask them to add it, then approve.",
                    _ => "The companion's account is gone; decline the request.",
                });
            var (result, issued) = await IssueCompanionCoreAsync(request.TicketId, companion!.FullName, companion.Email,
                _ => Task.FromResult<(Guid?, string?)>((companion.Id, null)),
                "approved companion request" + (note is null ? string.Empty : $": {note}"), overrideCapacity: false, actorUserId, ct);
            if (!result.Succeeded)
                return result;
            companionTicket = issued;
        }

        request.State = approve ? TicketCompanionRequestState.Approved : TicketCompanionRequestState.Declined;
        request.DecidedAt = clock.GetCurrentInstant();
        request.DecidedByUserId = actorUserId;
        request.DecisionNote = note;
        request.CompanionTicketId = companionTicket?.Id;
        await repository.UpdateCompanionRequestAsync(request, ct);

        var parent = await repository.GetTicketAsync(request.TicketId, ct);
        var summary = $"Companion request on ticket {parent?.Barcode} {(approve ? "approved" : "declined")}" +
            (note is null ? string.Empty : $": {note}");
        if (parent is not null)
            await TimelineAsync(parent.OrderId, TicketCheckoutOrderEventKind.CompanionRequestDecided, summary, actorUserId, ct);
        await auditLog.LogAsync(TicketsAuditActions.TicketCompanionRequestDecided, CompanionRequestAuditEntityType, request.Id,
            summary, actorUserId, request.HolderUserId, AuditSubjectTypes.User);
        return CheckoutMutationResult.Ok;
    }

    internal const string CompanionRequestAuditEntityType = "TicketCompanionRequest";

    private static bool CanAskCompanion(
        TicketCheckoutTicket ticket, TicketSalesEvent? salesEvent,
        IReadOnlyList<TicketCheckoutTicket> eventTickets, IReadOnlyList<TicketCompanionRequest> requests) =>
        ticket is { State: TicketCheckoutTicketState.Valid, Kind: TicketHolderKind.Adult, HolderUserId: not null }
        && salesEvent is { Mode: not TicketSalesMode.Closed, EffectiveSwitches.CompanionTicketsOn: true }
        && !eventTickets.Any(t => t.ParentTicketId == ticket.Id && t.Kind == TicketHolderKind.Companion && t.State == TicketCheckoutTicketState.Valid)
        && !requests.Any(r => r.TicketId == ticket.Id && r.State == TicketCompanionRequestState.Pending);
}
