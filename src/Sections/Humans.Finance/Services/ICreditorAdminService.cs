using Humans.Finance.Contracts;

namespace Humans.Finance.Services;

/// <summary>
/// The creditor surface <c>FinanceController</c> uses: the cross-section contract plus the two
/// binding writes an admin drives from /Finance/Creditors, which no other section calls and so
/// never cross the assembly boundary.
/// </summary>
internal interface ICreditorAdminService : ICreditorService
{
    /// <summary>Manually binds a member to an existing Holded creditor account (by 400000xx number).
    /// Resolves the Holded contact id. Fails — writing nothing — when the account is already bound to a
    /// different member, or when no Holded contact carries that supplier-account number.</summary>
    Task<CreditorBindResult> SetCreditorContactAsync(Guid userId, int supplierAccountNum, CancellationToken ct = default);

    /// <summary>Clears the member's creditor binding outright — the admin remedy for a wrong bind and
    /// for the duplicate the automatic write paths record rather than refuse. Removes the whole row,
    /// not just the 400000xx: a binding stripped of its number still carries the other member's Holded
    /// contact id, which merges their payables just as thoroughly. The member's next expense push
    /// re-resolves the contact from scratch. Returns false when there was nothing bound.</summary>
    Task<bool> ClearCreditorContactAsync(Guid userId, CancellationToken ct = default);
}
