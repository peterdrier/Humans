using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Services.Checkout;

/// <summary>What the gate reads: the scanned ticket, and the event's tickets for its name search and report.</summary>
internal sealed partial class TicketCheckoutService : ITicketGateLookup
{
    public async Task<IssuedTicketInfo?> FindIssuedTicketAsync(string? barcode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return null;
        if (await repository.GetTicketByBarcodeAsync(barcode.Trim(), ct) is not { } ticket)
            return null;
        if (await repository.GetSalesEventAsync(ticket.SalesEventId, ct) is not { } salesEvent)
            return null;

        var parent = ticket.ParentTicketId is { } parentId ? await repository.GetTicketAsync(parentId, ct) : null;
        return ToGateInfo(ticket, parent, salesEvent);
    }

    public async Task<IReadOnlyList<IssuedTicketInfo>> GetIssuedTicketsForEventAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        if (await repository.GetSalesEventForEventAsync(eventSettingsId, ct) is not { } salesEvent)
            return [];

        var tickets = await repository.GetTicketsForSalesEventAsync(salesEvent.Id, ct);
        var byId = tickets.ToDictionary(t => t.Id);
        return tickets
            .Select(t => ToGateInfo(t, t.ParentTicketId is { } parentId ? byId.GetValueOrDefault(parentId) : null, salesEvent))
            .ToList();
    }

    private IssuedTicketInfo ToGateInfo(TicketCheckoutTicket ticket, TicketCheckoutTicket? parent, TicketSalesEvent salesEvent)
    {
        var entryUserId = ticket.HolderUserId ?? parent?.HolderUserId;

        var label = ticket.ClassLabel ?? ticket.WaveName;
        var now = clock.GetCurrentInstant();
        var canStillSell = salesEvent.Mode != TicketSalesMode.Closed && !(salesEvent.SalesCloseAt is { } close && now >= close);
        // A child goes wherever its adult goes: while the adult's offer can sell, the child would be voided with it.
        var inResaleQueue = canStillSell
            && (ticket.State == TicketCheckoutTicketState.Offered || parent?.State == TicketCheckoutTicketState.Offered);
        return new IssuedTicketInfo(
            ticket.Id,
            salesEvent.EventSettingsId,
            ticket.Barcode,
            ticket.HolderName,
            ticket.HolderUserId,
            entryUserId,
            ticket.Kind == TicketHolderKind.Adult ? label : $"{ticket.Kind} · {label}",
            IsValid: !inResaleQueue
                && ticket.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered,
            InResaleQueue: inResaleQueue);
    }
}
