using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// Singleton row (Id = 1) tracking the operational state of HoldedSyncJob.
/// Mirrors TicketSyncState.
/// </summary>
public class HoldedSyncState
{
    public int Id { get; init; } = 1;
    public Instant? LastSyncAt { get; set; }
    public HoldedSyncStatus SyncStatus { get; set; } = HoldedSyncStatus.Idle;
    public string? LastError { get; set; }
    public Instant StatusChangedAt { get; set; }
    public int LastSyncedDocCount { get; set; }
}
