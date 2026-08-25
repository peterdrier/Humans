namespace Humans.Finance.Contracts;

/// <summary>
/// Read-only cross-section surface for Holded finance integration.
/// External sections use this interface for creditor status and account
/// lookup queries so write paths remain isolated behind
/// <see cref="IHoldedFinanceService"/>.
/// </summary>
public interface IHoldedFinanceServiceRead
{
    /// <summary>State of the last purchase-doc sync. Read-only; lazy-creates the state row.</summary>
    Task<HoldedDocSyncInfo> GetDocSyncInfoAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HoldedActualRow>> GetActualsForYearAsync(int calendarYear, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedUnmatchedRow>> GetUnmatchedAsync(CancellationToken ct = default);

    /// <summary>The active Holded expense-account id mapped to this budget category
    /// (holded_category_map), or null when no active mapping exists. Used to book a purchase
    /// line's `items[].account` directly at doc creation.</summary>
    Task<string?> GetHoldedAccountIdForCategoryAsync(Guid budgetCategoryId, CancellationToken ct = default);

    /// <summary>Derives cached creditor status (balance, owed, payments) for a member's 400000xx account
    /// from the cached daybook lines. Returns null when no lines are cached for the account.</summary>
    Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, CancellationToken ct = default);

    /// <summary>Admin overview: every 400000xx creditor account — cached balances, member bindings and
    /// the Holded contacts carrying the account names. Names are blank when Holded is unreachable.</summary>
    /// <returns>Two halves of one partition. <c>Unresolved</c> is the bindings with no 400000xx at all,
    /// which no account row can carry; nothing retries the resolution, so returning them here is what
    /// keeps them bindable (nobodies-collective/Humans#972).</returns>
    Task<(IReadOnlyList<HoldedCreditorAccountRow> Accounts, IReadOnlyList<CreditorContactBinding> Unresolved)>
        ListCreditorAccountsAsync(CancellationToken ct = default);

    /// <summary>The member's creditor-account binding, if any.</summary>
    Task<CreditorContactBinding?> GetCreditorContactByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Per-account statement: balance plus every journal line the Holded mirror holds for the
    /// account — no window of its own, so the span is whatever the last sync swept. Null when none.</summary>
    Task<HoldedCreditorLedger?> GetCreditorLedgerAsync(int supplierAccountNum, CancellationToken ct = default);
}
