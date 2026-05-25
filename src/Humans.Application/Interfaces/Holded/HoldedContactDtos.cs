using NodaTime;

namespace Humans.Application.Interfaces.Holded;

/// <summary>Create/update payload for a Holded contact (creditor/supplier).</summary>
public sealed record HoldedContactInput
{
    /// <summary>Legal name — the official identity (accountant / SEPA / tax). Never the burner.</summary>
    public required string Name { get; init; }
    /// <summary>Burner/display name. Only ever set alongside a legal <see cref="Name"/>.</summary>
    public string? TradeName { get; init; }
    /// <summary>Our stable handle — the Humans UserId.</summary>
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
    /// <summary>supplierRecord.num — the 400000xx supplier account number, or null if not yet assigned.</summary>
    public int? SupplierAccountNum { get; init; }
}

/// <summary>One row from GET accounting/v1/chartofaccounts.</summary>
public sealed record HoldedChartAccountDto
{
    public required int Num { get; init; }
    public required string Name { get; init; }
    /// <summary>Account balance. Negative on a 400000xx creditor account = money owed.</summary>
    public required decimal Balance { get; init; }
}

/// <summary>One row from GET invoicing/v1/payments.</summary>
public sealed record HoldedPaymentDto
{
    public required string Id { get; init; }
    public required string ContactId { get; init; }
    public required decimal Amount { get; init; }
    public required Instant Date { get; init; }
    public string? DocumentType { get; init; }
}
