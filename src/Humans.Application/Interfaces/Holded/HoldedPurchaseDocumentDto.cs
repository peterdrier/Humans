using NodaTime;

namespace Humans.Application.Interfaces.Holded;

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
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record HoldedPurchaseDocumentInput
{
    public required string ContactName { get; init; }
    public required Instant Date { get; init; }
    public required IReadOnlyList<HoldedPurchaseDocumentLineInput> Lines { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Description { get; init; }
}

public sealed record HoldedAttachmentInput
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required Stream Content { get; init; }
}
