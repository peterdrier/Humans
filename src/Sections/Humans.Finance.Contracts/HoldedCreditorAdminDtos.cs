
namespace Humans.Finance.Contracts;

/// <summary>One row of the admin creditor-accounts overview: a cached 400000xx balance + its member bindings.</summary>
/// <param name="Bindings">
/// Every member bound here, oldest first — not just one. Two members on a single 400000xx point
/// one person's expense payments at the other's creditor account, and the automatic write paths
/// record what Holded assigned rather than refusing, so the overview must show the collision
/// rather than pick a winner (nobodies-collective/Humans#975).
/// </param>
public sealed record HoldedCreditorAccountRow(
    int SupplierAccountNum,
    string Name,                    // Holded account name (legal name for member creditors)
    decimal? Balance,               // signed; negative = org owes
    decimal OwedToMember,           // = max(0, -Balance)
    IReadOnlyList<CreditorContactBinding> Bindings);

/// <summary>Outcome of a manual creditor-account bind: the failure message is admin-facing.</summary>
public sealed record CreditorBindResult(bool Succeeded, string? ErrorMessage)
{
    public static CreditorBindResult Success { get; } = new(true, null);

    public static CreditorBindResult Failure(string message) => new(false, message);
}

/// <summary>A member's binding to their Holded creditor account (DTO projection of the entity).</summary>
public sealed record CreditorContactBinding(
    Guid UserId,
    string HoldedContactId,
    int? SupplierAccountNum,
    CreditorContactSource Source);

/// <summary>Per-account statement: balance plus itemized journal lines (credit = owed/in, debit = paid/out).</summary>
/// <param name="Contact">The Holded contact holding this account, for the statement header. Null
/// when the cached contact list carries no contact for the number — including when Holded is
/// unreachable, which must cost the header and not the statement.</param>
public sealed record HoldedCreditorLedger(
    int SupplierAccountNum,
    string? Name,
    decimal? Balance,
    decimal OwedToMember,
    IReadOnlyList<CreditorLedgerLine> Lines,
    HoldedContactInfo? Contact = null);

/// <summary>The Holded contact behind a creditor account, as shown on the statement header. Every
/// field is optional — Holded fills what the contact record happens to carry.</summary>
public sealed record HoldedContactInfo(
    string? Name,
    string? TradeName,
    string? Email,
    string? Phone,
    string? Mobile,
    string? Iban,
    string? TaxCode,
    string? Address);
