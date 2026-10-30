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

    /// <summary>One account's lines, optionally narrowed to a date window (inclusive <paramref name="from"/>,
    /// exclusive <paramref name="toExclusive"/>) and one <c>type</c>.</summary>
    Task<IReadOnlyList<HoldedLedgerLine>> GetLedgerLinesByAccountNumAsync(
        int accountNum, Instant? from = null, Instant? toExclusive = null, string? type = null,
        CancellationToken ct = default);

    /// <summary>Every line of the given entries, on any account.</summary>
    Task<IReadOnlyList<HoldedLedgerLine>> GetLedgerLinesByEntryNumbersAsync(
        IReadOnlyCollection<int> entryNumbers, CancellationToken ct = default);

    /// <summary>Σdebit, Σcredit and line count per account over the whole mirror, grouped in the database.</summary>
    Task<IReadOnlyList<HoldedAccountTotals>> GetAccountTotalsAsync(CancellationToken ct = default);

    /// <summary>Σdebit and Σcredit per (account, type) for accounts in [<paramref name="fromAccount"/>,
    /// <paramref name="toAccount"/>].</summary>
    Task<IReadOnlyList<HoldedAccountTypeTotals>> GetAccountTypeTotalsAsync(
        int fromAccount, int toAccount, CancellationToken ct = default);

    /// <summary>Σdebit − Σcredit per account over [<paramref name="from"/>, <paramref name="toExclusive"/>).
    /// Accounts with no line in the window are absent.</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetSumsByAccountAsync(
        IReadOnlyCollection<int> accounts, Instant from, Instant toExclusive, CancellationToken ct = default);

    Task<(int Lines, int Entries)> GetMirrorCountsAsync(CancellationToken ct = default);

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

internal sealed record HoldedAccountTotals(int AccountNum, decimal Debit, decimal Credit, int LineCount)
{
    public decimal Balance => Debit - Credit;
}

internal sealed record HoldedAccountTypeTotals(int AccountNum, string? Type, decimal Debit, decimal Credit);
