using Humans.Base.Interfaces;

namespace Humans.Finance.Contracts;

public interface IHoldedFinanceService : IApplicationService
{
    Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default);
    Task<int> ProvisionAsync(int blockStart, bool addAll, CancellationToken ct = default);
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);

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

    /// <summary>Manually binds a member to an existing Holded creditor account by 400000xx number.
    /// Fails, writing nothing, when the account is already bound or no Holded contact carries it.</summary>
    Task<CreditorBindResult> SetCreditorContactAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);

    /// <summary>Per-account statement: balance plus every journal line the Holded mirror holds for the
    /// account — no window of its own, so the span is whatever the last sync swept. Null when none.</summary>
    Task<HoldedCreditorLedger?> GetCreditorLedgerAsync(int supplierAccountNum, CancellationToken ct = default);

    /// <summary>Ensures the member has a Holded creditor contact and binding, returning the contact id.
    /// Reuses the existing binding, else the seed from a prior report, else creates a new contact. A
    /// Manual binding is never downgraded to Auto.</summary>
    Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, CancellationToken ct = default);

    /// <summary>Records the resolved 400000xx number on the member's binding (once the payable exists).</summary>
    Task SetCreditorAccountNumAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);

    /// <summary>Clears the member's creditor binding — the remedy for a wrong bind or a collision. Removes
    /// the whole row, not just the number: the contact id alone merges two members' payables just as
    /// thoroughly. The next push re-resolves. False when nothing was bound.</summary>
    Task<bool> ClearCreditorContactAsync(Guid userId, CancellationToken ct = default);
}
