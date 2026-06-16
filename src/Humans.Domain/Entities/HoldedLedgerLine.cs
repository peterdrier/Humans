using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// One cached Holded daybook (dailyledger) journal line — the single source of truth for creditor
/// activity. Everything is derived from these facts: balance = Σdebit − Σcredit;
/// owed = max(0, Σcredit − Σdebit); payments (outs) = debit lines; ins = credit lines.
/// Scoped to creditor accounts (400000xx) at sync time. Idempotent on (<see cref="EntryNumber"/>, <see cref="Line"/>).
/// </summary>
public class HoldedLedgerLine
{
    public Guid Id { get; init; }

    /// <summary>Holded journal entry number. Together with <see cref="Line"/> uniquely identifies the line.</summary>
    public int EntryNumber { get; set; }

    /// <summary>Line index within the journal entry.</summary>
    public int Line { get; set; }

    /// <summary>Literal 400000xx supplier-account number this line posts to.</summary>
    public int AccountNum { get; set; }

    /// <summary>Entry timestamp (the journal line's date).</summary>
    public Instant Date { get; set; }

    /// <summary>Holded line type, e.g. "purchase", "payment".</summary>
    public string? Type { get; set; }

    public string? Description { get; set; }

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }

    public Instant LastSyncedAt { get; set; }

    public Instant CreatedAt { get; init; }
}
