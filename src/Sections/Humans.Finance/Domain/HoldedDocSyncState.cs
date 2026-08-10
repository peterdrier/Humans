using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// Status of the purchase-document sync (Finance's own concern — doc matching feeds the
/// unmatched queue). The ledger mirror's sync state moved to the Holded section with the
/// mirror itself; this singleton replaces the old shared <c>holded_sync_states</c> row.
/// Lazy-created on first sync — no seed.
/// </summary>
internal sealed class HoldedDocSyncState
{
    public int Id { get; init; } = 1;
    public Instant? LastSyncAt { get; set; }
    public string Status { get; set; } = "Idle";   // "Idle" | "Running" | "Error"
    public string? LastError { get; set; }
    public Instant? StatusChangedAt { get; set; }
    public int LastSyncedDocCount { get; set; }
}
