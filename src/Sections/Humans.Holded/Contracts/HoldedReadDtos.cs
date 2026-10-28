using NodaTime;

namespace Humans.Holded.Contracts;

public sealed record HoldedExpenseAccountDto
{
    public required string Id { get; init; }
    public required int AccountNum { get; init; }
    public required string Name { get; init; }
}

public sealed record HoldedPurchaseLineDto
{
    public required decimal Amount { get; init; }      // line `price`
    public string? AccountId { get; init; }            // line `account` (Holded account id)
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>From GET v2/ledger-entries. <c>account</c> is the 8-digit ledger number, not a Holded id;
/// amounts are euros. <c>date</c> arrives as DD/MM/YYYY and is parsed to Madrid midnight.</summary>
public sealed record HoldedLedgerLineDto
{
    public required int EntryNumber { get; init; }
    public required int Line { get; init; }
    public required Instant Date { get; init; }        // `date` (dd/MM/yyyy, Madrid midnight)
    public required int AccountNum { get; init; }      // `account`
    public required decimal Debit { get; init; }
    public required decimal Credit { get; init; }
    public string? Type { get; init; }                 // e.g. "purchase", "payment"
    public string? Description { get; init; }
}

public sealed record HoldedAccountDto
{
    public required string Id { get; init; }
    public required int Number { get; init; }
    public required string Name { get; init; }
    public string? Group { get; init; }
    public required decimal Debit { get; init; }
    public required decimal Credit { get; init; }
    public required decimal Balance { get; init; }
    public bool Archived { get; init; }
}

public sealed record HoldedUsageDto
{
    public required string Period { get; init; }         // "2026-08"
    public required long Usage { get; init; }
    public required long Limit { get; init; }
    public IReadOnlyDictionary<string, long> SecondaryUsages { get; init; }
        = new Dictionary<string, long>(StringComparer.Ordinal);
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
    /// <summary>`payments_pending` — what is still unpaid on this doc. An absent field reads as 0,
    /// i.e. nothing owed, which is the safe direction: it refuses a booking rather than over-paying.</summary>
    public decimal PaymentsPending { get; init; }
    // v2 list items carry no approval timestamp (only the single GET does) but do carry `draft`. Null when
    // Holded doesn't report it, which Finance treats as not yet approved.
    public bool? IsDraft { get; init; }
    public string Currency { get; init; } = "eur";
    public IReadOnlyList<string> Tags { get; init; } = []; // doc-level tags
    public IReadOnlyList<HoldedPurchaseLineDto> Lines { get; init; } = [];
}
