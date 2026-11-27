using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain.Checkout;
using NodaTime;

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
        var now = clock.GetCurrentInstant();
        var holds = await LoadResaleHoldsAsync(ticketing.Id, now, ct);
        return ToGateInfo(ticket, parent, ticketing, holds, now);
    }

    public async Task<IReadOnlyList<IssuedTicketInfo>> GetIssuedTicketsForEventAsync(
        Guid eventSettingsId, CancellationToken ct = default)
    {
        if (await repository.GetTicketingSettingsForEventAsync(eventSettingsId, ct) is not { } ticketing)
            return [];

        var tickets = await repository.GetTicketsForSettingsAsync(ticketing.Id, ct);
        var byId = tickets.ToDictionary(t => t.Id);
        var now = clock.GetCurrentInstant();
        var holds = await LoadResaleHoldsAsync(ticketing.Id, now, ct);
        return tickets
            .Select(t => ToGateInfo(t, t.ParentTicketId is { } parentId ? byId.GetValueOrDefault(parentId) : null, ticketing, holds, now))
            .ToList();
    }

    private async Task<(HashSet<Guid> Sold, HashSet<Guid> Reserved)> LoadResaleHoldsAsync(
        Guid settingsId, Instant now, CancellationToken ct)
    {
        var offers = await repository.GetOffersForSettingsAsync(settingsId, ct);
        var sold = offers.Where(o => o.State == TicketResaleOfferState.Sold).Select(o => o.TicketId).ToHashSet();
        var reservations = offers.Where(o => o.State == TicketResaleOfferState.Reserved).ToList();
        if (reservations.Count == 0)
            return (sold, []);

        var orders = (await repository.GetOrdersForSettingsAsync(settingsId, ct)).ToDictionary(o => o.Id);
        // An absent owner cannot prove the checkout closed; recovery must resolve it before admission.
        var reserved = reservations.Where(o => o.ReservedByOrderId is not { } orderId
                || !orders.TryGetValue(orderId, out var order) || order.HoldsTickets(now))
            .Select(o => o.TicketId).ToHashSet();
        return (sold, reserved);
    }

    private static IssuedTicketInfo ToGateInfo(
        TicketCheckoutTicket ticket, TicketCheckoutTicket? parent, TicketingSettings ticketing,
        (HashSet<Guid> Sold, HashSet<Guid> Reserved) holds, Instant now)
    {
        var entryUserId = ticket.HolderUserId ?? parent?.HolderUserId;

        var label = ticket.ClassLabel ?? ticket.WaveName;
        var adultId = ticket.ParentTicketId ?? ticket.Id;
        var sold = holds.Sold.Contains(adultId);
        var held = ticketing.CancelledAt is null && ticket.IsHeld
            && !TicketPiiTombstone.IsTombstoned(ticket.HolderName, ticket.HolderEmail)
            && (ticket.ParentTicketId is null || (parent is { IsHeld: true }
                && !TicketPiiTombstone.IsTombstoned(parent.HolderName, parent.HolderEmail)));
        // Sales closing does not cancel a reserved checkout or finish an interrupted sale.
        var inResaleQueue = held && !sold && (holds.Reserved.Contains(adultId)
            || (!ticketing.IsClosed(now)
                && (ticket.State == TicketCheckoutTicketState.Offered || parent?.State == TicketCheckoutTicketState.Offered)));
        return new IssuedTicketInfo(
            ticket.Id,
            ticketing.EventSettingsId,
            ticket.Barcode,
            ticket.HolderName,
            ticket.HolderUserId,
            entryUserId,
            ticket.Kind == TicketHolderKind.Adult ? label : $"{ticket.Kind} · {label}",
            IsValid: held && !sold && !inResaleQueue,
            InResaleQueue: inResaleQueue);
    }
}
