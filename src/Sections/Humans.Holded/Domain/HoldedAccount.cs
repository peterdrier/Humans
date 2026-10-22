using NodaTime;

namespace Humans.Holded.Domain;

/// <summary>Holded's own running totals are the reconciliation target: an account whose balance disagrees with the
/// mirrored Σdebit − Σcredit gets a targeted re-pull.</summary>
internal sealed class HoldedAccount
{
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
