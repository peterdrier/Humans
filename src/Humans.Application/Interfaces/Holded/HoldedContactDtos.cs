namespace Humans.Application.Interfaces.Holded;

/// <summary>Create/update payload for a Holded contact (creditor/supplier).</summary>
public sealed record HoldedContactInput
{
    /// <summary>Legal name — the official identity (accountant / SEPA / tax). Never the burner.</summary>
    public required string Name { get; init; }
    /// <summary>Burner/display name. Only ever set alongside a legal <see cref="Name"/>.</summary>
    public string? TradeName { get; init; }
    /// <summary>Our stable handle — the Humans UserId. Not sent to Holded: v2's contacts POST/PUT
    /// have no `custom_id` field (it is read-only on GET). Kept on the DTO for now since nothing
    /// reads it back for lookup; no functional loss.</summary>
    public string? CustomId { get; init; }
    /// <summary>Holded contact type. Creditors/suppliers get a 400000xx account.</summary>
    public string Type { get; init; } = "creditor";
    public string? Iban { get; init; }
    /// <summary>When set, update this existing contact rather than create a new one.</summary>
    public string? ExistingContactId { get; init; }
}

/// <summary>A Holded contact as returned by GET contacts/{id}.</summary>
public sealed record HoldedContactDto
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    /// <summary>supplier_record.num — the 400000xx supplier account number, or null if not yet assigned.</summary>
    public int? SupplierAccountNum { get; init; }

    // Contact-info fields for the creditor-statement header (plan Task 8b).
    public string? TradeName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Mobile { get; init; }
    public string? Iban { get; init; }
    /// <summary>Tax identification number — v2 `code`.</summary>
    public string? TaxCode { get; init; }
    /// <summary>One display string assembled from bill_address's non-empty parts (address, postal
    /// code, city, province, country), or null when bill_address is absent/empty.</summary>
    public string? Address { get; init; }
}
