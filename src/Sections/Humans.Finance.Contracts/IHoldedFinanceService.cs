using Humans.Base.Interfaces;

namespace Humans.Finance.Contracts;

/// <summary>Cross-section Holded reads; writes are on <see cref="IHoldedFinanceWrite"/>.</summary>
public interface IHoldedFinanceService : IApplicationService
{
    /// <summary>Lazy-creates the state row.</summary>
    Task<HoldedDocSyncInfo> GetDocSyncInfoAsync(CancellationToken ct = default);

    /// <summary>Approved expense lines against the Holded document and ledger mirrors. Local data only: no Holded
    /// call, no write. <paramref name="year"/> narrows lines and orphans to their document year; members stay
    /// all-time.</summary>
    Task<ExpenseReconReport> GetExpenseReconAsync(int? year, CancellationToken ct = default);

    Task<IReadOnlyList<HoldedActualRow>> GetActualsForYearAsync(int calendarYear, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedUnmatchedRow>> GetUnmatchedAsync(CancellationToken ct = default);

    /// <summary>The active Holded expense-account id mapped to this budget category, or null when there is none.</summary>
    Task<string?> GetHoldedAccountIdForCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default);

    /// <summary>The category map's accounts plus Finance's managed registry. <paramref name="activeOnly"/> drops retired
    /// managed accounts and orphaned category accounts; without it they are listed as inactive, so old reports still get
    /// a label. Cache reads only.</summary>
    Task<IReadOnlyList<HoldedExpenseAccountOption>> ListExpenseAccountsAsync(
        bool activeOnly, CancellationToken ct = default);

    /// <summary>Derived from the cached daybook lines. Null when none are cached for the account.</summary>
    Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, CancellationToken ct = default);

    /// <summary>Every 400000xx creditor account. Names are blank when Holded is unreachable.</summary>
    /// <returns><c>Unresolved</c> is the bindings with no 400000xx, which no account row can carry. Nothing retries
    /// their resolution, so returning them here is what keeps them bindable.</returns>
    Task<(IReadOnlyList<HoldedCreditorAccountRow> Accounts, IReadOnlyList<CreditorContactBinding> Unresolved)>
        ListCreditorAccountsAsync(CancellationToken ct = default);

    Task<CreditorContactBinding?> GetCreditorContactByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every journal line the Holded mirror holds for the account, with no window of its own. Null when
    /// none.</summary>
    Task<HoldedCreditorLedger?> GetCreditorLedgerAsync(int supplierAccountNum, CancellationToken ct = default);

    /// <summary>Active and archived rows: what each budget category is actually booked to today.</summary>
    Task<IReadOnlyList<HoldedCategoryMapRow>> GetCategoryMapAsync(CancellationToken ct = default);

    /// <summary>The rows <c>/Finance/Sepa</c> renders, without the candidate-line halves that need a live bank-feed
    /// read. <c>UnavailableReason</c> is set when missing configuration turns booking off for the whole screen.</summary>
    Task<(IReadOnlyList<SepaPayoutTransferRow> Transfers, string? UnavailableReason)>
        GetSepaTransfersAsync(CancellationToken ct = default);
}
