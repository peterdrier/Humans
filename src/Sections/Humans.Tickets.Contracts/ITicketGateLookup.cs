using Humans.Base.Interfaces;

namespace Humans.Tickets.Contracts;

/// <summary>The gate's read of tickets Humans issued itself, not TicketTailor's. Live, never cached.</summary>
public interface ITicketGateLookup : IApplicationService
{
    /// <summary>Exact match on the code the QR carries, any event and any state; null for a blank or unknown code.</summary>
    Task<IssuedTicketInfo?> FindIssuedTicketAsync(string? barcode, CancellationToken ct = default);

    /// <summary>
    /// Every ticket issued for the event, in any state, unordered: the gate's name search and arrivals report. Empty
    /// when the event has no ticketing settings.
    /// </summary>
    Task<IReadOnlyList<IssuedTicketInfo>> GetIssuedTicketsForEventAsync(Guid eventSettingsId, CancellationToken ct = default);
}

/// <summary>
/// <paramref name="InResaleQueue"/>: the ticket, or the adult ticket a child's rides on, is in resale and can still
/// sell, so it is not valid until the holder withdraws it (else it could admit someone and then sell or be voided);
/// an unresolved reservation keeps it blocked after sales close.
/// <paramref name="IsValid"/> is false immediately when the event is cancelled, before background retirement finishes.
/// Erased identity markers on the ticket or its adult also make it invalid, including legacy rows still marked valid.
/// <paramref name="EntryUserId"/> is the holder, or for a child or companion ticket without an account the adult it
/// belongs to: whose Early Entry applies. Both null is an adult ticket whose holder has no account.
/// </summary>
public sealed record IssuedTicketInfo(
    Guid Id,
    Guid EventSettingsId,
    string Barcode,
    string HolderName,
    Guid? HolderUserId,
    Guid? EntryUserId,
    string TicketLabel,
    bool IsValid,
    bool InResaleQueue = false);
