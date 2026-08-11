namespace Humans.Finance.Models;

/// <summary>A row of the admin Creditor Accounts overview: a Holded 400000xx balance + its member
/// bindings. More than one binding is a collision — two members' expense payments pointed at one
/// creditor account — and the row renders it as such rather than picking a winner.</summary>
/// <param name="Balance">Inverted for display: credit − debit, so a positive figure is money the
/// organisation owes the member. The mirror and every derivation elsewhere keep Holded's own sign
/// (Σdebit − Σcredit); the flip happens here and nowhere else. /Holded/Accounts/{num} shows the
/// same account unflipped.</param>
internal sealed record CreditorAccountRowVm(
    int SupplierAccountNum,
    string Name,
    decimal? Balance,
    IReadOnlyList<CreditorAccountBindingVm> Bindings)
{
    /// <summary>Two or more members on one 400000xx — needs an admin to unbind all but the owner.</summary>
    public bool HasCollision => Bindings.Count > 1;

    /// <summary>Sort key for the Member column. Unbound rows sort last rather than first: an admin
    /// sorting by member is looking for people, not for gaps.</summary>
    public string MemberSortKey => Bindings.Count == 0
        ? "￿"
        : string.Join(", ", Bindings.Select(b => b.MemberName)).ToUpperInvariant();
}

/// <summary>One member bound to a creditor account, named for display and unbindable by id.</summary>
internal sealed record CreditorAccountBindingVm(Guid UserId, string MemberName, string Source);

/// <summary>The /Finance/Creditors page model: the per-account overview plus the bindings that never
/// resolved a 400000xx and so have no account row to sit on (nobodies-collective/Humans#972).</summary>
/// <param name="SortBy">Active column — "account" (default), "name", "balance" or "member".</param>
/// <param name="SortDir">"asc" or "desc"; the headers link to the opposite of whatever is active.</param>
internal sealed record CreditorsPageVm(
    IReadOnlyList<CreditorAccountRowVm> Accounts,
    IReadOnlyList<UnresolvedCreditorBindingVm> Unresolved,
    string SortBy,
    string SortDir);

/// <summary>A member whose creditor-account number never resolved — no automatic retry exists, so this
/// is the discoverability surface for binding them manually via POST /Finance/Creditors/Bind.</summary>
internal sealed record UnresolvedCreditorBindingVm(Guid UserId, string MemberName, string Source);
