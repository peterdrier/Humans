namespace Humans.Finance.Domain;

/// <summary>
/// One credit transfer inside a generated payout file. The row id is what the file's
/// <c>EndToEndId</c> is derived from, so it is the handle the bank and Holded share.
/// </summary>
internal sealed class SepaPayoutTransfer
{
    /// <summary>Also the source of this transfer's <c>EndToEndId</c>; never changes.</summary>
    public Guid Id { get; init; }

    public Guid FileId { get; init; }

    /// <summary>The member paid. Bare FK (no nav).</summary>
    public Guid UserId { get; init; }

    /// <summary>The 400000xx/410000xx creditor account the balance was read from.</summary>
    public int SupplierAccountNum { get; init; }

    /// <summary>Legal name as written into <c>Cdtr/Nm</c>, already SEPA-normalized.</summary>
    public string CreditorName { get; init; } = "";

    /// <summary>Unmasked — this row and the XML are the only places it is kept.</summary>
    public string Iban { get; init; } = "";

    /// <summary>What every log, audit entry and screen shows instead of <see cref="Iban"/>.</summary>
    public string IbanMasked { get; init; } = "";

    public decimal Amount { get; init; }
}
