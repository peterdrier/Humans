using NodaTime;

namespace Humans.Application.Interfaces.Holded;

/// <summary>A P&L expense account from Holded (`expensesaccounts` / chart).</summary>
public sealed record HoldedExpenseAccountDto
{
    public required string Id { get; init; }
    public required int AccountNum { get; init; }
    public required string Name { get; init; }
}

/// <summary>One purchase-document line: carries its booked account id and tags.</summary>
public sealed record HoldedPurchaseLineDto
{
    public required decimal Amount { get; init; }      // line `price`
    public string? AccountId { get; init; }            // line `account` (Holded account id)
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A purchase document as returned by the list endpoint.</summary>
public sealed record HoldedPurchaseDocListItemDto
{
    public required string Id { get; init; }
    public required string DocNumber { get; init; }
    public required string ContactName { get; init; }
    public required Instant Date { get; init; }        // doc `date` (epoch s)
    public required decimal Subtotal { get; init; }
    public required decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public Instant? ApprovedAt { get; init; }
    public string Currency { get; init; } = "eur";
    public IReadOnlyList<string> Tags { get; init; } = []; // doc-level tags
    public IReadOnlyList<HoldedPurchaseLineDto> Lines { get; init; } = [];
}
