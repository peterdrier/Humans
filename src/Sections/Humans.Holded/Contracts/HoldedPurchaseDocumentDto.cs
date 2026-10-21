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
    /// <summary><c>notes</c> — the doc's notes field, null when blank or absent. Only this single
    /// GET returns it; the v2 purchase list never does (verified live 2026-10-21).</summary>
    public string? Notes { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary><c>payments_detail</c>: one entry per payment recorded on the document, in Holded's
    /// order. The only place a payment's id can be read — <c>POST purchases/{id}/payments</c> answers
    /// <c>{"status":"paid"}</c> and nothing else (verified live 2026-10-18).</summary>
    public IReadOnlyList<HoldedPaymentDetailDto> Payments { get; init; } = [];
}

/// <summary>One payment on a purchase document, as <c>payments_detail[]</c> reports it:
/// <c>{"id","amount","date","bank_id"}</c>. <paramref name="Date"/> is the plain <c>YYYY-MM-DD</c>
/// the payment was posted with; <paramref name="BankId"/> is the treasury account it left.</summary>
public sealed record HoldedPaymentDetailDto(string Id, decimal Amount, LocalDate Date, string? BankId);

public sealed record HoldedPurchaseDocumentLineInput
{
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    /// <summary>The mapped Holded expense-account id (`holded_category_map.HoldedAccountId`) to book
    /// this line to — the doc is booked to the right department from creation, so no retag is ever
    /// needed later. Null when the report's category has no active mapping.</summary>
    public string? AccountId { get; init; }
}

public sealed record HoldedPurchaseDocumentInput
{
    public required string ContactName { get; init; }
    /// <summary>The Holded contact to link the purchase doc to — required by v2's POST /purchases.</summary>
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
