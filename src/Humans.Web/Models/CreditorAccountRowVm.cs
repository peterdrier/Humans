namespace Humans.Web.Models;

/// <summary>A row of the admin Creditor Accounts overview: a Holded 400000xx balance + its member binding.</summary>
public sealed record CreditorAccountRowVm(
    int SupplierAccountNum,
    string Name,
    decimal? Balance,
    decimal OwedToMember,
    Guid? BoundUserId,
    string? BoundMemberName,
    string? BindingSource);
