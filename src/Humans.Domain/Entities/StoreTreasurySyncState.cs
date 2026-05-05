using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class StoreTreasurySyncState
{
    public int Id { get; set; } = 1;
    public Instant? LastSyncAt { get; set; }
    public StoreTreasurySyncStatus SyncStatus { get; set; } = StoreTreasurySyncStatus.Idle;
    public string? LastError { get; set; }
}
