using Humans.Application.Interfaces.Holded;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Finance.Dtos;

/// <summary>One row of the admin creditor-accounts overview: a cached 400000xx balance + its member binding.</summary>
public sealed record HoldedCreditorAccountRow(
    int SupplierAccountNum,
    string Name,                    // Holded account name (legal name for member creditors)
    decimal? Balance,               // signed; negative = org owes
    decimal OwedToMember,           // = max(0, -Balance)
    Guid? BoundUserId,              // the Humans member bound to this account, if any
    CreditorContactSource? BindingSource);

/// <summary>A member's binding to their Holded creditor account (DTO projection of the entity).</summary>
public sealed record CreditorContactBinding(
    Guid UserId,
    string HoldedContactId,
    int? SupplierAccountNum,
    CreditorContactSource Source);

/// <summary>Per-account statement: balance plus itemized journal lines (credit = owed/in, debit = paid/out).</summary>
public sealed record HoldedCreditorLedger(
    int SupplierAccountNum,
    string? Name,
    decimal? Balance,
    decimal OwedToMember,
    IReadOnlyList<HoldedLedgerLineDto> Lines);
