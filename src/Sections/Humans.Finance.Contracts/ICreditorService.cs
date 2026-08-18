using Humans.Application.Interfaces;

namespace Humans.Finance.Contracts;

/// <summary>
/// What other sections may ask of Finance's creditor bindings. Binding and unbinding are admin
/// operations and are deliberately absent — they live on the section's internal
/// <c>ICreditorAdminService</c>, which only <c>FinanceController</c> sees.
/// </summary>
public interface ICreditorService : IApplicationService
{
    /// <summary>Derives cached creditor status (balance, owed, payments) for a member's 400000xx account
    /// from the cached daybook lines. Returns null when no lines are cached for the account.</summary>
    Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, CancellationToken ct = default);

    /// <summary>Admin overview: every 400000xx creditor account — cached balances, member bindings and
    /// Holded's own creditor contacts (which carry the account name and appear before any journal
    /// activity exists). Names are blank when Holded is unreachable; the rest still renders.</summary>
    /// <returns>
    /// The two halves of one partition of the binding set, from one snapshot of the same inputs.
    /// <c>Unresolved</c> is the remainder <c>Accounts</c> cannot represent: bindings whose 400000xx
    /// never resolved — neither our one-shot push resolution nor Holded's live contact list has a
    /// number for the contact — so there is no account row to place them on. There is no automatic
    /// retry (nobodies-collective/Humans#972), so returning them here is what keeps them visible on
    /// /Finance/Creditors, where an admin binds them.
    /// </returns>
    Task<(IReadOnlyList<HoldedCreditorAccountRow> Accounts, IReadOnlyList<CreditorContactBinding> Unresolved)>
        ListCreditorAccountsAsync(CancellationToken ct = default);

    /// <summary>The member's creditor-account binding, if any.</summary>
    Task<CreditorContactBinding?> GetCreditorContactByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Per-account statement: balance + itemized journal lines over the last ~year. Null if unknown.</summary>
    Task<HoldedCreditorLedger?> GetCreditorLedgerAsync(int supplierAccountNum, CancellationToken ct = default);

    /// <summary>Ensures the member has a Holded creditor contact + binding, returning the contact id.
    /// Reuses the existing binding (PUT-updates the contact), else adopts <paramref name="seedContactId"/>
    /// (lazy-seed from a prior pushed report), else creates a new Holded contact. The binding is the
    /// single source of truth for the member→account link; a Manual binding is never downgraded to Auto.</summary>
    Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, CancellationToken ct = default);

    /// <summary>Records the resolved 400000xx number on the member's binding (once the payable exists).</summary>
    Task SetCreditorAccountNumAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);
}
