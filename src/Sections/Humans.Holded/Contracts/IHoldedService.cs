using Humans.Base.Interfaces;

namespace Humans.Holded.Contracts;

/// <summary>Reads never call Holded.</summary>
public interface IHoldedService : IApplicationService
{
    Task<IReadOnlyList<HoldedLedgerLineInfo>> GetLedgerLinesAsync(int accountNum, CancellationToken ct = default);

    /// <summary>Σdebit − Σcredit over the whole mirror, for every account with cached lines. Raw bookkeeping sign,
    /// before any display flip.</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetAccountBalancesAsync(CancellationToken ct = default);

    /// <summary>False when a sweep was already running and this one was skipped. <paramref name="full"/> false:
    /// trailing window anchored on now; true or a cold cache: inception to today. Both refresh the accounts cache,
    /// reconcile totals with targeted re-pulls, and drain the API call log.</summary>
    Task<bool> SyncLedgerAsync(bool full = false, CancellationToken ct = default);
}
