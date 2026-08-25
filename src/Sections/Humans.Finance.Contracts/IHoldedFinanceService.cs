using Humans.Base.Interfaces;

namespace Humans.Finance.Contracts;

public interface IHoldedFinanceService : IApplicationService, IHoldedFinanceServiceRead
{
    Task<HoldedProvisioningPlan> GetProvisioningPlanAsync(int blockStart, CancellationToken ct = default);
    Task<int> ProvisionAsync(int blockStart, bool addAll, CancellationToken ct = default);
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);

    /// <summary>Manually binds a member to an existing Holded creditor account by 400000xx number.
    /// Fails, writing nothing, when the account is already bound or no Holded contact carries it.</summary>
    Task<CreditorBindResult> SetCreditorContactAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);

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
