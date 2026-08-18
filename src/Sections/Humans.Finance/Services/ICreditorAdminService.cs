using Humans.Finance.Contracts;

namespace Humans.Finance.Services;

/// <summary>
/// The creditor surface <c>FinanceController</c> uses: the cross-section contract plus the two
/// binding writes an admin drives from /Finance/Creditors, which never leave this project.
/// </summary>
internal interface ICreditorAdminService : ICreditorService
{
    /// <summary>Binds a member to an existing Holded creditor account by 400000xx. Refuses, writing
    /// nothing, when the account or its contact already belongs to someone else.</summary>
    Task<CreditorBindResult> SetCreditorContactAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);

    /// <summary>Removes the member's binding row outright — the remedy for a wrong bind or a
    /// collision. False when there was nothing bound.</summary>
    Task<bool> ClearCreditorContactAsync(Guid userId, CancellationToken ct = default);
}
