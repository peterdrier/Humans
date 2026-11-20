using NodaTime;

namespace Humans.Holded.Services;

internal sealed record HoldedPurchaseDocumentDto
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
internal sealed record HoldedPaymentDetailDto(string Id, decimal Amount, LocalDate Date, string? BankId);

