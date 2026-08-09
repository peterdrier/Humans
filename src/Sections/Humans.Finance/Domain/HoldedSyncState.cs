using Humans.Finance.Domain;
using NodaTime;
namespace Humans.Finance.Domain;

internal sealed class HoldedSyncState
{
    public int Id { get; init; } = 1;
    public Instant? LastSyncAt { get; set; }
    public HoldedSyncStatus SyncStatus { get; set; } = HoldedSyncStatus.Idle;
    public string? LastError { get; set; }
    public Instant? StatusChangedAt { get; set; }
    public int LastSyncedDocCount { get; set; }
}
