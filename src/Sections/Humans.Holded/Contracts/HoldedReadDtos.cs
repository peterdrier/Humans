using NodaTime;

namespace Humans.Holded.Contracts;

public sealed record HoldedPurchaseLineDto
{
    public string? AccountId { get; init; }            // line `account` (Holded account id)
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record HoldedPurchaseDocListItemDto
{
    public required string Id { get; init; }
    public required string DocNumber { get; init; }
    public string? ContactId { get; init; }            // `contact_id` — who the doc is payable to
    public required string ContactName { get; init; }
    /// <summary>The doc's internal description; null when blank.</summary>
    public string? Description { get; init; }
    public required Instant Date { get; init; }        // doc `date` (ISO yyyy-MM-dd, Madrid midnight)
    public required decimal Subtotal { get; init; }
    public required decimal Tax { get; init; }
    public required decimal Total { get; init; }
    /// <summary>`payments_pending` — what is still unpaid on this doc. Required when <c>draft</c> is false;
    /// an absent balance on a draft or unknown approval state reads as 0, and those docs are not bookable.</summary>
    public decimal PaymentsPending { get; init; }
    // v2 list items carry no approval timestamp (only the single GET does) but do carry `draft`. Null when
    // Holded doesn't report it, which Finance treats as not yet approved.
    public bool? IsDraft { get; init; }
    public string Currency { get; init; } = "eur";
    public IReadOnlyList<string> Tags { get; init; } = []; // doc-level tags
    public IReadOnlyList<HoldedPurchaseLineDto> Lines { get; init; } = [];
}
