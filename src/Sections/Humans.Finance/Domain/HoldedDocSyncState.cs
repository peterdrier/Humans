using NodaTime;

namespace Humans.Finance.Domain;

/// <summary>The ledger mirror's sync state is the Holded section's. Singleton, lazy-created on first sync.</summary>
internal sealed class HoldedDocSyncState
{
    public int Id { get; init; } = 1;
    public Instant? LastSyncAt { get; set; }
    public string Status { get; set; } = "Idle";   // "Idle" | "Running" | "Error"
    public string? LastError { get; set; }
    public Instant? StatusChangedAt { get; set; }
    public int LastSyncedDocCount { get; set; }
}
