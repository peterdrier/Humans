using NodaTime;

namespace Humans.Store.Domain;

internal sealed class TreasurySyncState
{
    public int Id { get; set; } = 1;
    public Instant? LastSyncAt { get; set; }
    public TreasurySyncStatus SyncStatus { get; set; } = TreasurySyncStatus.Idle;
    public string? LastError { get; set; }
}
