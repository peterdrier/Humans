using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Finance.Contracts;

public interface IHoldedFinanceService : IApplicationService, IHoldedFinanceServiceRead
{
    Task<HoldedSyncResult> SyncAsync(CancellationToken ct = default);

    /// <summary>Resolves an expense account for a caller that has no budget category: an explicit
    /// <paramref name="existingAccountNum"/> is verified against the live chart and returned; else an
    /// account whose name normalizes equal to <paramref name="name"/> is returned; else one is created
    /// at the next free number of the <c>62900100</c> block. Accounts outside the budget map are
    /// recorded in Finance's managed registry so the doc sync attributes their docs.
    /// Throws <see cref="InvalidOperationException"/> when the explicit number is not in Holded.</summary>
    [ExternalWrite]
    Task<HoldedExpenseAccountRef> CreateOrLinkExpenseAccountAsync(
        string name, int? existingAccountNum, CancellationToken ct = default);

    /// <summary>Retires or restores a managed account so it drops out of, or returns to, the active
    /// pickers. No-op for a budget-category account or a number Finance does not manage — the
    /// account itself is never touched in Holded.</summary>
    Task SetExpenseAccountActiveAsync(int accountNum, bool isActive, CancellationToken ct = default);

    /// <summary>Ensures the member has a Holded creditor contact and binding, returning the contact id.
    /// Reuses the existing binding, else the seed from a prior report, else creates a new contact. A
    /// linked contact is never updated in Holded (its PUT is a full replacement that resets the
    /// creditor account). A Manual binding is never downgraded to Auto.</summary>
    [ExternalWrite]
    Task<string> EnsureCreditorContactAsync(
        Guid userId, string legalName, string? burnerName, string? iban,
        string? seedContactId, int? seedAccountNum, CancellationToken ct = default);

    /// <summary>Records the resolved 400000xx number on the member's binding (once the payable exists).</summary>
    Task SetCreditorAccountNumAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);
}
