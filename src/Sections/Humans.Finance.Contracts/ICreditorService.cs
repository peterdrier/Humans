using Humans.Application.Interfaces;

namespace Humans.Finance.Contracts;

/// <summary>
/// Finance's creditor surface for other sections. Binding and unbinding are admin operations and
/// stay off this contract. Every rule below is stated once, in Docs/Finance.md.
/// </summary>
public interface ICreditorService : IApplicationService
{
    /// <summary>Balance, owed and payment figures for a member's 400000xx, derived from the cached
    /// daybook. Null when no lines are cached — unknown, not settled.</summary>
    Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, CancellationToken ct = default);

    /// <summary>Every 400000xx account with its bindings, plus the bindings whose account never
    /// resolved and so have no row to sit on. Account names go blank when Holded is unreachable.</summary>
    Task<(IReadOnlyList<HoldedCreditorAccountRow> Accounts, IReadOnlyList<CreditorContactBinding> Unresolved)>
        ListCreditorAccountsAsync(CancellationToken ct = default);

    /// <summary>The member's creditor-account binding, if any.</summary>
    Task<CreditorContactBinding?> GetCreditorContactByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Per-account statement: balance plus itemized journal lines. Null if unknown.</summary>
    Task<HoldedCreditorLedger?> GetCreditorLedgerAsync(int supplierAccountNum, CancellationToken ct = default);

    /// <summary>Ensures the member has a Holded creditor contact and binding, returning the contact id.
    /// Reuses the bound contact, else the seed from a prior report, else creates one.</summary>
    Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, CancellationToken ct = default);

    /// <summary>Records the resolved 400000xx on the member's binding, once the payable exists.</summary>
    Task SetCreditorAccountNumAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);
}
