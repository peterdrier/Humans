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

    /// <summary>Holded's journal-entry id (<c>id</c>): the same on every line of one entry, so not a line key.</summary>
    public string? EntryId { get; set; }

    /// <summary>Holded's manual tick in its ledger view (<c>checked</c>), not bank reconciliation. Nullable so the
    /// column could be added to the existing table; null means the line was last synced before Humans read it.</summary>
    public bool? Checked { get; set; }

    public Instant LastSyncedAt { get; set; }

    public Instant CreatedAt { get; init; }
}
