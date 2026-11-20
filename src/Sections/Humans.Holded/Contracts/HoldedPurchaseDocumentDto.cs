using NodaTime;

namespace Humans.Holded.Contracts;

public sealed record HoldedPurchaseDocumentLineInput
{
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    /// <summary>Booked to the right department from creation, so no retag is ever needed. Null when the
    /// report's category has no active mapping.</summary>
    public string? AccountId { get; init; }
    /// <summary>Holded tax keys. Null leaves them out, so the contact's default tax applies; empty books the line
    /// with no tax at all.</summary>
    public IReadOnlyList<string>? Taxes { get; init; }
}

public sealed record HoldedPurchaseDocumentInput
{
    public required string ContactName { get; init; }
    /// <summary>Required by v2's POST /purchases.</summary>
    public required string ContactId { get; init; }
    public required Instant Date { get; init; }
    public required IReadOnlyList<HoldedPurchaseDocumentLineInput> Lines { get; init; }
    public string? Description { get; init; }
    /// <summary>The document number to give it; null lets Holded number it.</summary>
    public string? Number { get; init; }
    /// <summary>Internal notes — visible to us, not printed on the document.</summary>
    public string? Notes { get; init; }
}

public sealed record HoldedAttachmentInput
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required Stream Content { get; init; }
}

/// <summary><paramref name="Date"/> is the plain <c>YYYY-MM-DD</c> the payment was posted with;
/// <paramref name="BankId"/> is the treasury account it left.</summary>
public sealed record HoldedPaymentDetailDto(string Id, decimal Amount, LocalDate Date, string? BankId);
