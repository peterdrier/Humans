namespace Humans.Web.Models;

/// <summary>A row of the admin Creditor Accounts overview: a Holded 400000xx balance + its member
/// bindings. More than one binding is a collision — two members' expense payments pointed at one
/// creditor account — and the row renders it as such rather than picking a winner.</summary>
public sealed record CreditorAccountRowVm(
    int SupplierAccountNum,
    string Name,
    decimal? Balance,
    decimal OwedToMember,
    IReadOnlyList<CreditorAccountBindingVm> Bindings);

/// <summary>One member bound to a creditor account, named for display and unbindable by id.</summary>
public sealed record CreditorAccountBindingVm(Guid UserId, string MemberName, string Source);
