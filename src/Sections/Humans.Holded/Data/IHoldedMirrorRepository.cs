using Humans.Base.Interfaces.Repositories;
using Humans.Holded.Domain;
using NodaTime;

namespace Humans.Holded.Data;

internal interface IHoldedMirrorRepository : IRepository
{
    // Ledger mirror
    /// <summary>Upserts the fetched window on (EntryNumber, Line) and deletes local rows inside
    /// [from,to] (optionally one account) absent from <paramref name="rows"/> — the fetch is the
    /// truth for its window. An EMPTY <paramref name="rows"/> list is a valid sweep result and
    /// deletes everything cached in the window; it does not early-return. Append-only caching is
    /// the bug this replaces: deleted and reclassified Holded lines lingered forever.</summary>
    Task ReplaceLedgerWindowAsync(Instant from, Instant to, int? accountNum,
        IReadOnlyList<HoldedLedgerLine> rows, Instant now, CancellationToken ct = default);

    Task<IReadOnlyList<HoldedLedgerLine>> GetLedgerLinesByAccountNumAsync(int accountNum, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedLedgerLine>> GetAllLedgerLinesAsync(CancellationToken ct = default);

    /// <summary>False only on a cold cache, which forces the sync's full-history sweep.</summary>
    Task<bool> HasAnyLedgerLinesAsync(CancellationToken ct = default);

    // Chart of accounts
    Task UpsertAccountsAsync(IReadOnlyList<HoldedAccount> rows, Instant now, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedAccount>> GetAccountsAsync(CancellationToken ct = default);

    // API call metering
    Task AddApiCallsAsync(IReadOnlyList<HoldedApiCall> rows, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedApiCall>> GetApiCallsAsync(CancellationToken ct = default);

    // Sync state (one row per kind, lazy-created)
    Task<HoldedSyncState> GetOrCreateSyncStateAsync(HoldedSyncKind kind, CancellationToken ct = default);
    Task SaveSyncStateAsync(HoldedSyncState state, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedSyncState>> GetSyncStatesAsync(CancellationToken ct = default);
}
