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
        if (await repository.GetTicketingSettingsAsync(ticket.TicketingSettingsId, ct) is not { } ticketing)
            return null;

        var parent = ticket.ParentTicketId is { } parentId ? await repository.GetTicketAsync(parentId, ct) : null;
        return ToGateInfo(ticket, parent, ticketing);
    }

    public async Task<IReadOnlyList<IssuedTicketInfo>> GetIssuedTicketsForEventAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        if (await repository.GetTicketingSettingsForEventAsync(eventSettingsId, ct) is not { } ticketing)
            return [];

        var tickets = await repository.GetTicketsForSettingsAsync(ticketing.Id, ct);
        var byId = tickets.ToDictionary(t => t.Id);
        return tickets
            .Select(t => ToGateInfo(t, t.ParentTicketId is { } parentId ? byId.GetValueOrDefault(parentId) : null, ticketing))
            .ToList();
    }

    private IssuedTicketInfo ToGateInfo(TicketCheckoutTicket ticket, TicketCheckoutTicket? parent, TicketingSettings ticketing)
    {
        var entryUserId = ticket.HolderUserId ?? parent?.HolderUserId;

        var label = ticket.ClassLabel ?? ticket.WaveName;
        var now = clock.GetCurrentInstant();
        var canStillSell = !ticketing.IsClosed(now);
        // A child goes wherever its adult goes: while the adult's offer can sell, the child would be voided with it.
        var inResaleQueue = canStillSell
            && (ticket.State == TicketCheckoutTicketState.Offered || parent?.State == TicketCheckoutTicketState.Offered);
        return new IssuedTicketInfo(
            ticket.Id,
            ticketing.EventSettingsId,
            ticket.Barcode,
            ticket.HolderName,
            ticket.HolderUserId,
            entryUserId,
            ticket.Kind == TicketHolderKind.Adult ? label : $"{ticket.Kind} · {label}",
            IsValid: !inResaleQueue && ticket.IsHeld && (ticket.ParentTicketId is null || parent?.IsHeld == true),
            InResaleQueue: inResaleQueue);
    }
}
