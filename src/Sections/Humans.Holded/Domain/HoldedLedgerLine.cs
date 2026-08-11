using NodaTime;

namespace Humans.Holded.Domain;

/// <summary>
/// One mirrored Holded journal line, for <em>every</em> account in the chart — not just the
/// creditor block. Balances and statements are derived from these facts: balance =
/// Σdebit − Σcredit. Idempotent on (<see cref="EntryNumber"/>, <see cref="Line"/>).
/// </summary>
/// <remarks>
/// The mirror is re-derivable and window-replaced, never append-only: a sweep that no longer
/// returns a line deletes it. Filtering by account before the upsert is what let deleted and
/// reclassified lines linger forever, so nothing here is filtered on the way in.
/// </remarks>
internal sealed class HoldedLedgerLine
{
    public Guid Id { get; init; }

    /// <summary>Holded journal entry number. Together with <see cref="Line"/> uniquely identifies the line.</summary>
    public int EntryNumber { get; set; }

    /// <summary>Line index within the journal entry.</summary>
    public int Line { get; set; }

    /// <summary>The literal chart-of-accounts number this line posts to.</summary>
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
