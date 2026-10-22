using NodaTime;

namespace Humans.Holded.Domain;

/// <summary>
/// Every account in the chart, not just the creditor block. Window-replaced, never append-only: a sweep that no
/// longer returns a line deletes it, and nothing is filtered by account on the way in.
/// </summary>
internal sealed class HoldedLedgerLine
{
    public Guid Id { get; init; }

    /// <summary>Unique together with <see cref="Line"/>.</summary>
    public int EntryNumber { get; set; }

    public int Line { get; set; }

    public int AccountNum { get; set; }

    public Instant Date { get; set; }

    public string? Type { get; set; }

    public string? Description { get; set; }

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }

    public Instant LastSyncedAt { get; set; }

    public Instant CreatedAt { get; init; }
}
