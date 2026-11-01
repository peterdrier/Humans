using NodaTime;

namespace Humans.Holded.Contracts;

public sealed record HoldedPurchaseDocumentDto
{
    public required string Id { get; init; }
    public required string DocNumber { get; init; }
    public required decimal Subtotal { get; init; }
    public required decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public required decimal PaymentsTotal { get; init; }
    public required decimal PaymentsPending { get; init; }
    public Instant? ApprovedAt { get; init; }
    /// <summary>Null when blank. Only this single GET returns it; the v2 purchase list never does.</summary>
    public string? Notes { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>In Holded's order. The only place a payment's id can be read: <c>POST purchases/{id}/payments</c>
    /// answers <c>{"status":"paid"}</c> and nothing else.</summary>
    public IReadOnlyList<HoldedPaymentDetailDto> Payments { get; init; } = [];
}

/// <summary><paramref name="Date"/> is the plain <c>YYYY-MM-DD</c> the payment was posted with;
/// <paramref name="BankId"/> is the treasury account it left.</summary>
public sealed record HoldedPaymentDetailDto(string Id, decimal Amount, LocalDate Date, string? BankId);

public sealed record HoldedPurchaseDocumentLineInput
{
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    /// <summary>Booked to the right department from creation, so no retag is ever needed. Null when the
    /// report's category has no active mapping.</summary>
    public string? AccountId { get; init; }
}

public sealed record HoldedPurchaseDocumentInput
{
    public required string ContactName { get; init; }
    /// <summary>Required by v2's POST /purchases.</summary>
    public required string ContactId { get; init; }
    public required Instant Date { get; init; }
    public required IReadOnlyList<HoldedPurchaseDocumentLineInput> Lines { get; init; }
    public string? Description { get; init; }
    /// <summary>Internal notes — visible to us, not printed on the document.</summary>
    public string? Notes { get; init; }
}

public sealed record HoldedAttachmentInput
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required Stream Content { get; init; }
}
