using Humans.Application.Services.Finance.Dtos;

namespace Humans.Application.Interfaces.Finance;

public interface IHoldedFinanceService : IApplicationService
{
    Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default);
    Task<int> ProvisionAsync(int blockStart, bool addAll, CancellationToken ct = default);
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HoldedActualRow>> GetActualsForYearAsync(int calendarYear, CancellationToken ct = default);
    Task<IReadOnlyList<HoldedUnmatchedRow>> GetUnmatchedAsync(CancellationToken ct = default);

    /// <summary>Nightly cache refresh: creditor balances (chartofaccounts) + payments rows.</summary>
    Task SyncCreditorDataAsync(CancellationToken ct = default);

    /// <summary>Reads cached creditor status for a member by their supplier-account number + contact id.
    /// Returns null when nothing is cached yet (not registered in Holded).</summary>
    Task<HoldedCreditorStatus?> GetCreditorStatusAsync(
        int? supplierAccountNum, string holdedContactId, CancellationToken ct = default);

    /// <summary>Admin overview: every cached 400000xx creditor balance joined with its member binding.</summary>
    Task<IReadOnlyList<HoldedCreditorAccountRow>> ListCreditorAccountsAsync(CancellationToken ct = default);

    /// <summary>The member's creditor-account binding, if any.</summary>
    Task<CreditorContactBinding?> GetCreditorContactByUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Manually binds a member to an existing Holded creditor account (by 400000xx number).
    /// Resolves the Holded contact id; returns false if no contact carries that supplier-account number.</summary>
    Task<bool> SetCreditorContactAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);

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
