using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Finance.Contracts;

/// <summary>Cross-section writes: expense-account resolution for workgroups and creditor-contact binding for expense
/// payouts.</summary>
public interface IHoldedFinanceWrite : IApplicationService
{
    /// <summary>For a caller with no budget category: an explicit <paramref name="existingAccountNum"/> is verified
    /// against the live chart; else an account whose normalized name equals <paramref name="name"/>; else a new one at
    /// the next free number of the <c>62900100</c> block. Non-budget accounts go into Finance's managed registry so the
    /// doc sync attributes their docs. Throws <see cref="InvalidOperationException"/> when the explicit number is not
    /// in Holded.</summary>
    [ExternalWrite]
    Task<HoldedExpenseAccountRef> CreateOrLinkExpenseAccountAsync(
        string name, int? existingAccountNum, CancellationToken ct = default);

    /// <summary>Only affects the pickers; the account is never touched in Holded. No-op for an account Finance does not
    /// manage.</summary>
    Task SetExpenseAccountActiveAsync(int accountNum, bool isActive, CancellationToken ct = default);

    /// <summary>Reuses the existing binding, else the seed from a prior report, else creates a contact. A linked contact
    /// is never updated in Holded: its PUT is a full replacement that resets the creditor account. A Manual binding is
    /// never downgraded to Auto. <paramref name="jobName"/> is the audit actor when a concurrent push forces this call
    /// to retract a contact it created.</summary>
    [ExternalWrite]
    Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, string jobName, CancellationToken ct = default);

    /// <summary>Called once the payable exists. A number that differs from the binding's is audited under
    /// <paramref name="jobName"/>.</summary>
    Task SetCreditorAccountNumAsync(Guid userId, int supplierAccountNum, string jobName, CancellationToken ct = default);
}
