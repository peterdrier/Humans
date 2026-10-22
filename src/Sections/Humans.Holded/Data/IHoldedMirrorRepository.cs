using Humans.Base.Interfaces.Repositories;
using Humans.Holded.Domain;
using NodaTime;

namespace Humans.Holded.Data;

internal interface IHoldedMirrorRepository : IRepository
{
    /// <summary>The fetch is the truth for its window: rows inside [from,to] (optionally one account) absent from
    /// <paramref name="rows"/> are deleted. An empty <paramref name="rows"/> deletes everything cached in the
    /// window; it does not early-return.</summary>
    Task ReplaceLedgerWindowAsync(Instant from, Instant to, int? accountNum,
        IReadOnlyList<HoldedLedgerLine> rows, Instant now, CancellationToken ct = default);

    Task<IReadOnlyList<HoldedLedgerLine>> GetLedgerLinesByAccountNumAsync(int accountNum, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedLedgerLine>> GetAllLedgerLinesAsync(CancellationToken ct = default);

    /// <summary>False only on a cold cache, which forces the sync's full-history sweep.</summary>
    Task<bool> HasAnyLedgerLinesAsync(CancellationToken ct = default);

    Task UpsertAccountsAsync(IReadOnlyList<HoldedAccount> rows, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedAccount>> GetAccountsAsync(CancellationToken ct = default);

    Task AddApiCallsAsync(IReadOnlyList<HoldedApiCall> rows, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedApiCall>> GetApiCallsAsync(CancellationToken ct = default);

    Task<HoldedSyncState> GetOrCreateSyncStateAsync(HoldedSyncKind kind, CancellationToken ct = default);
    Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedSyncState>> GetSyncStatesAsync(CancellationToken ct = default);
}
