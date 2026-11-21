using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Services.Checkout;

/// <summary>What the gate reads: the scanned ticket.</summary>
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

        var entryUserId = ticket.HolderUserId;
        if (entryUserId is null && ticket.ParentTicketId is { } parentId)
            entryUserId = (await repository.GetTicketAsync(parentId, ct))?.HolderUserId;

        var label = ticket.ClassLabel ?? ticket.WaveName;
        var now = clock.GetCurrentInstant();
        var canStillSell = salesEvent.Mode != TicketSalesMode.Closed && !(salesEvent.SalesCloseAt is { } close && now >= close);
        var inResaleQueue = ticket.State == TicketCheckoutTicketState.Offered && canStillSell;
        return new IssuedTicketInfo(
            ticket.Id,
            salesEvent.EventSettingsId,
            ticket.Barcode,
            ticket.HolderName,
            ticket.HolderUserId,
            entryUserId,
            ticket.Kind == TicketHolderKind.Adult ? label : $"{ticket.Kind} · {label}",
            IsValid: ticket.State == TicketCheckoutTicketState.Valid
                || (ticket.State == TicketCheckoutTicketState.Offered && !canStillSell),
            InResaleQueue: inResaleQueue);
    }
}
