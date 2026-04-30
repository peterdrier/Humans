using NodaTime;

namespace Humans.Domain.Entities;

public class StoreTreasurySyncState
{
    public int Id { get; set; } = 1;
    public Instant? LastSyncAt { get; set; }
    public string SyncStatus { get; set; } = "Idle";
    public string? LastError { get; set; }
}
