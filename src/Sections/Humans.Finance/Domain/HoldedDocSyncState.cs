using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>
/// Status of the purchase-document sync only (the ledger mirror's sync state is the Holded
/// section's). Singleton, lazy-created on first sync — no seed.
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
