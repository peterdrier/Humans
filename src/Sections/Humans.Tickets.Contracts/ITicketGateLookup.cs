using Humans.Base.Interfaces;

namespace Humans.Tickets.Contracts;

/// <summary>The gate's read of tickets Humans issued itself through its checkout. Live, never cached.</summary>
public interface ITicketGateLookup : IApplicationService
{
    /// <summary>Exact match on the code the QR carries, any event and any state; null for a blank or unknown code.</summary>
    Task<IssuedTicketInfo?> FindIssuedTicketAsync(string? barcode, CancellationToken ct = default);
}

/// <summary>
/// <paramref name="IsValid"/> includes a ticket waiting in the resale queue. <paramref name="EntryUserId"/> is the
/// holder, or for a child or companion ticket without an account the adult it belongs to: whose Early Entry applies.
/// </summary>
public sealed record IssuedTicketInfo(
    Guid Id,
    Guid EventSettingsId,
    string Barcode,
    string HolderName,
    Guid? HolderUserId,
    Guid? EntryUserId,
    string TicketLabel,
    bool IsValid);
