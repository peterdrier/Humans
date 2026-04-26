namespace Humans.Application.DTOs.Finance;

/// <summary>
/// Dashboard projection of the singleton <c>HoldedSyncState</c> plus the live
/// unmatched-doc count, used to render the sync state card on /Finance.
/// </summary>
public sealed record HoldedSyncDashboardDto(
    NodaTime.Instant? LastSyncAt,
    Humans.Domain.Enums.HoldedSyncStatus SyncStatus,
    string? LastError,
    int LastSyncedDocCount,
    int UnmatchedCount);
