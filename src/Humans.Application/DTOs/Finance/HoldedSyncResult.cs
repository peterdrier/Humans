namespace Humans.Application.DTOs.Finance;

/// <summary>
/// Outcome summary returned by <see cref="Humans.Application.Interfaces.Finance.IHoldedSyncService.SyncAsync"/>.
/// Reports total docs fetched, total matched/unmatched after match resolution,
/// and a per-status breakdown keyed by <see cref="Humans.Domain.Enums.HoldedMatchStatus"/> name.
/// </summary>
public sealed record HoldedSyncResult(
    int DocsFetched,
    int Matched,
    int Unmatched,
    IReadOnlyDictionary<string, int> ByStatus);
