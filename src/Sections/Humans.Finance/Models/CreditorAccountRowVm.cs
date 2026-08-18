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
    IReadOnlyList<CreditorBindingVm> Bindings)
{
    /// <summary>Two or more members on one 400000xx — needs an admin to unbind all but the owner.</summary>
    public bool HasCollision => Bindings.Count > 1;

    /// <summary>Sort key for the Member column. Unbound rows sort last rather than first: an admin
    /// sorting by member is looking for people, not for gaps.</summary>
    public string MemberSortKey => Bindings.Count == 0
        ? "￿"
        : string.Join(", ", Bindings.Select(b => b.MemberName)).ToUpperInvariant();
}

/// <summary>One member bound to a creditor account, named for display and unbindable by id. Used
/// for both halves of the page: the bindings on an account row, and the ones with no account yet.</summary>
internal sealed record CreditorBindingVm(Guid UserId, string MemberName, string Source);

/// <summary>The /Finance/Creditors page model.</summary>
/// <param name="Unresolved">Bindings with no 400000xx, so no account row to sit on. Nothing retries
/// the resolution, so this is the only place they can be bound (nobodies-collective/Humans#972).</param>
/// <param name="SortBy">Active column — "account" (default), "name", "balance" or "member".</param>
/// <param name="SortDir">"asc" or "desc"; the headers link to the opposite of whatever is active.</param>
internal sealed record CreditorsPageVm(
    IReadOnlyList<CreditorAccountRowVm> Accounts,
    IReadOnlyList<CreditorBindingVm> Unresolved,
    string SortBy,
    string SortDir);

