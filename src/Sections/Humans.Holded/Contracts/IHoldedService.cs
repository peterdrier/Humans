using Humans.Base.Interfaces;

namespace Humans.Holded.Contracts;

/// <summary>
/// The Holded section's cross-section surface: cached reads over the ledger mirror, plus the
/// sync that refills it. Reads never call Holded.
/// </summary>
public interface IHoldedService : IApplicationService
{
    /// <summary>Cached journal lines for one account. Zero Holded calls.</summary>
    Task<IReadOnlyList<HoldedLedgerLineInfo>> GetLedgerLinesAsync(int accountNum, CancellationToken ct = default);

    /// <summary>Per-account balance (Σdebit − Σcredit) over the whole mirror, for every account
    /// with cached lines. Raw bookkeeping convention, before any display flip. Zero Holded calls.</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetAccountBalancesAsync(CancellationToken ct = default);

    /// <summary>Ledger mirror sync; false when a sweep was already running and this one was skipped.
    /// full=false: trailing window anchored on now. full=true / cold cache: inception → today.
    /// Both refresh the accounts cache, reconcile per-account totals with targeted re-pulls, and
    /// drain the API call log.</summary>
    Task<bool> SyncLedgerAsync(bool full = false, CancellationToken ct = default);
}
