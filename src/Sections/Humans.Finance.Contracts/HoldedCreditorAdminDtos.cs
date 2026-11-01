
using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>A cached 400000xx creditor account and its member bindings.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><paramref name="Balance"/> is signed: negative means the organization owes. <paramref name="OwedToMember"/> is
/// <c>max(0, -Balance)</c>.</item>
/// <item><paramref name="Bindings"/> lists every member bound here: the automatic write paths record a collision rather
/// than refuse it, so the overview shows it instead of picking a winner.</item>
/// <item><paramref name="IbanMasked"/> is masked because this row reaches a screen; the raw IBAN stays inside Finance,
/// in the payout record and the SEPA file.</item>
/// <item><paramref name="OpenDocumentCount"/> counts the approved, still-owing purchase documents a payout would pay
/// against. Null on an unbound or colliding row, or when Holded's document list could not be read.</item>
/// <item><paramref name="PendingPayoutFileName"/> names a SEPA file with an unbooked, unwithdrawn transfer to this
/// account. While set the row is not payable: the balance is still open in Holded, so a second file would pay it
/// twice.</item>
/// <item><paramref name="HasDuplicateDocuments"/>: an expense line of the bound member matches more than one Holded
/// purchase document, inflating the balance, so the row is not payable. Null when the check could not run, which
/// the payout also refuses on.</item>
/// </list>
/// </remarks>
public sealed record HoldedCreditorAccountRow(
    int SupplierAccountNum,
    string Name,
    decimal? Balance,
    decimal OwedToMember,
    IReadOnlyList<CreditorContactBinding> Bindings,
    string? IbanMasked = null,
    int? OpenDocumentCount = null,
    string? PendingPayoutFileName = null,
    bool? HasDuplicateDocuments = false);

public sealed record CreditorContactBinding(
    Guid UserId,
    string HoldedContactId,
    int? SupplierAccountNum,
    CreditorContactSource Source)
{
    public Instant CreatedAt { get; init; }
}

/// <summary>Credit = owed to the member, debit = paid out.</summary>
/// <remarks>
/// A null <paramref name="Contact"/> (none cached, including when Holded is down) costs the header, not the statement.
/// </remarks>
public sealed record HoldedCreditorLedger(
    int SupplierAccountNum,
    decimal Balance,
    decimal OwedToMember,
    IReadOnlyList<CreditorLedgerLine> Lines,
    HoldedContactInfo? Contact = null);

/// <summary>Every field is optional: Holded fills what the contact record carries.</summary>
public sealed record HoldedContactInfo(
    string? Name,
    string? TradeName,
    string? Email,
    string? Phone,
    string? Mobile,
    string? Iban,
    string? TaxCode,
    string? Address);
