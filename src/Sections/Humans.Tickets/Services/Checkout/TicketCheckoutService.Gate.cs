using Humans.Gate.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;

namespace Humans.Tickets.Services.Checkout;

/// <summary>What the gate reads: the scanned ticket, and the ticket rules a holder has not accepted.</summary>
internal sealed partial class TicketCheckoutService : ITicketGateLookup, IGateEntryRequirementSource
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
        return new IssuedTicketInfo(
            ticket.Id,
            salesEvent.EventSettingsId,
            ticket.Barcode,
            ticket.HolderName,
            ticket.HolderUserId,
            entryUserId,
            ticket.Kind == TicketHolderKind.Adult ? label : $"{ticket.Kind} · {label}",
            IsValid: ticket.State is TicketCheckoutTicketState.Valid or TicketCheckoutTicketState.Offered);
    }

    /// <summary>
    /// A holder who never accepted the event's ticket rules online (a partner the buyer named, or a rules change since)
    /// signs the paper copy at the gate.
    /// </summary>
    public async Task<IReadOnlyList<GateEntryRequirement>> GetOutstandingAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default)
    {
        if (await repository.GetSalesEventForEventAsync(eventSettingsId, ct) is not { TermsTeamId: { } termsTeam })
            return [];

        var rows = await consents.GetRequiredConsentRowsForUserAsync(userId, termsTeam, ct);
        return rows.Where(r => !r.Signed)
            .Select(r => new GateEntryRequirement(
                $"ticket-rules:{r.DocumentVersionId}", $"Sign the paper copy of “{r.Title}”"))
            .ToList();
    }
}
