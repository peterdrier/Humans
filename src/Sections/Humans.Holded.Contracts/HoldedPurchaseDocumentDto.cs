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
    public IReadOnlyList<string> Tags { get; init; } = [];
}

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
}

public sealed record HoldedAttachmentInput
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required Stream Content { get; init; }
}
