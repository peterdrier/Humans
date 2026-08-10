using NodaTime;

namespace Humans.Holded.Domain;

/// <summary>
/// One chart-of-accounts entry as Holded reports it, including Holded's own running totals.
/// Those totals are the reconciliation target: an account whose Holded balance disagrees with
/// the mirrored lines' Σdebit − Σcredit gets a targeted re-pull.
/// </summary>
internal sealed class HoldedAccount
{
    /// <summary>PK — the literal chart number, e.g. 40000004.</summary>
    public int Number { get; init; }

    public string HoldedId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Group { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Balance { get; set; }
    public bool Archived { get; set; }
    public Instant SyncedAt { get; set; }
}
