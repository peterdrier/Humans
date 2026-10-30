namespace Humans.Holded.Contracts;

/// <summary>Create payload for a Holded contact (creditor/supplier/client). There is no update payload; see
/// <see cref="IHoldedClient.CreateContactAsync"/>.</summary>
public sealed record HoldedContactInput
{
    /// <summary>Legal name — the official identity (accountant / SEPA / tax). Never the burner.</summary>
    public required string Name { get; init; }
    /// <summary>Burner/display name. Only ever set alongside a legal <see cref="Name"/>.</summary>
    public string? TradeName { get; init; }
    /// <summary>The Humans UserId. Not sent: v2's contacts POST has no <c>custom_id</c> (read-only on GET).</summary>
    public string? CustomId { get; init; }
    /// <summary>Decides the account series Holded mints: <c>creditor</c> 410xxxxx, <c>supplier</c> 400xxxxx,
    /// <c>client</c>/<c>debtor</c> 430xxxxx. Expense-report members are creditors, apart from the 400 proveedores
    /// paid by invoice; Store's factura counterparties are clients.</summary>
    public string Type { get; init; } = "creditor";
    public string? Iban { get; init; }
    /// <summary>Tax identification number — v2 `code`. NIF/CIF for Spanish counterparties; a
    /// home-country tax id or passport number for foreign camps (no NIF is required of them).</summary>
    public string? TaxCode { get; init; }
    public string? Email { get; init; }
    /// <summary>Street address — sent as `bill_address.address`. Required on a full factura.</summary>
    public string? Address { get; init; }
    /// <summary>ISO 3166-1 alpha-2 — sent as `bill_address.country_code`.</summary>
    public string? CountryCode { get; init; }
}

public sealed record HoldedContactDto
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    /// <summary>v2 <c>custom_id</c>: read-only on the API, set only on contacts created through v1 or Holded's app.</summary>
    public string? CustomId { get; init; }
    /// <summary>v2 <c>type</c>: <c>creditor</c>, <c>supplier</c>, <c>client</c>, <c>debtor</c>…</summary>
    public string? Type { get; init; }
    /// <summary>supplier_record.num — the 400000xx supplier account number, or null if not yet assigned.</summary>
    public int? SupplierAccountNum { get; init; }

    public string? TradeName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Mobile { get; init; }
    public string? Iban { get; init; }
    /// <summary>v2 <c>code</c>.</summary>
    public string? TaxCode { get; init; }
    /// <summary>One display string assembled from bill_address's non-empty parts (address, postal
    /// code, city, province, country), or null when bill_address is absent/empty.</summary>
    public string? Address { get; init; }
}
