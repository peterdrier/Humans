using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>Reads never call Holded. Amounts are Holded's raw bookkeeping sign (Σdebit − Σcredit) unless a member
/// says otherwise; callers flip for display.</summary>
public interface IHoldedService : IApplicationService
{
    /// <summary>False without <c>HOLDED_API_KEY_V2</c> (PR previews, local dev): every Holded call would 401, so
    /// callers skip the work instead of writing off their queues. The mirror's reads still answer.</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<HoldedLedgerLineInfo>> GetLedgerLinesAsync(int accountNum, CancellationToken ct = default);

    /// <summary>Σdebit − Σcredit over the whole mirror, for every account with cached lines. Raw bookkeeping sign,
    /// before any display flip.</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetAccountBalancesAsync(CancellationToken ct = default);

    /// <summary>Every leg of one journal entry, on any account, in line order. Empty when no cached line carries
    /// <paramref name="entryNumber"/>.</summary>
    Task<IReadOnlyList<HoldedLedgerLineInfo>> GetEntryAsync(int entryNumber, CancellationToken ct = default);

    /// <summary>Lines of type <c>purchase</c> on one account, in daybook order, optionally only those whose
    /// description starts with <paramref name="descriptionPrefix"/> (ordinal). A document's line carries Holded's
    /// own text, <c>"Purchase  "</c> plus the document's description, so match on that. For pairing approved
    /// expense lines by description and amount, counting duplicates.</summary>
    Task<IReadOnlyList<HoldedLedgerLineInfo>> GetPurchaseLinesAsync(
        int accountNum, string? descriptionPrefix = null, CancellationToken ct = default);

    /// <summary>What purchase documents leave open per account in [<paramref name="fromAccount"/>,
    /// <paramref name="toAccount"/>]: Σ (credit − debit) over <c>purchase</c>, <c>purchaserefund</c> and
    /// <c>payment</c> lines. Positive means owed by the association. Not the account balance: lines booked by hand
    /// (journal entries, collections) are left out. Accounts with none of the three types are absent.</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetOpenDocumentAmountsAsync(
        int fromAccount, int toAccount, CancellationToken ct = default);

    /// <summary>Σdebit − Σcredit per account over [<paramref name="from"/>, <paramref name="to"/>], both
    /// inclusive accounting dates. Every requested account is present; one with no line in the window is 0.</summary>
    Task<IReadOnlyDictionary<int, decimal>> GetSumsByAccountAsync(
        IReadOnlySet<int> accounts, LocalDate from, LocalDate to, CancellationToken ct = default);

    /// <summary>The whole chart from the mirror, archived accounts included. Consumers choose display order.</summary>
    Task<IReadOnlyList<HoldedChartAccountInfo>> GetChartAsync(CancellationToken ct = default);

    /// <summary>False when a sweep was already running and this one was skipped. <paramref name="full"/> false:
    /// trailing window anchored on now; true or a cold cache: inception to today. Both refresh the accounts cache,
    /// reconcile totals with targeted re-pulls, and drain the API call log.</summary>
    Task<bool> SyncLedgerAsync(bool full = false, CancellationToken ct = default);
}
