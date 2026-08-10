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

/// <summary>One journal line from GET v2/ledger-entries. Field names verified against the live
/// Holded API (2026-08-10): entry_number/line/date/account/debit/credit/type/description.
/// <c>account</c> is the literal 400000xx number (not an internal id); amounts are in euros.
/// <c>date</c> arrives as DD/MM/YYYY and is parsed to Madrid midnight.</summary>
public sealed record HoldedLedgerLineDto
{
    public required int EntryNumber { get; init; }
    public required int Line { get; init; }
    public required Instant Date { get; init; }        // `date` (dd/MM/yyyy, Madrid midnight)
    public required int AccountNum { get; init; }      // `account` — literal 400000xx
    public required decimal Debit { get; init; }
    public required decimal Credit { get; init; }
    public string? Type { get; init; }                 // e.g. "purchase", "payment"
    public string? Description { get; init; }
}

/// <summary>One accounting/chart-of-accounts account from GET v2/accounting-accounts, with its
/// current totals.</summary>
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

/// <summary>API usage/quota counters from GET v2/usage.</summary>
public sealed record HoldedUsageDto
{
    public required string Period { get; init; }         // "2026-08"
    public required long Usage { get; init; }
    public required long Limit { get; init; }
    public IReadOnlyDictionary<string, long> SecondaryUsages { get; init; }
        = new Dictionary<string, long>(StringComparer.Ordinal);
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
    // v2 list items carry no approval timestamp (only the single-GET does — see
    // HoldedPurchaseDocumentDto.ApprovedAt); approval state here comes from
    // IHoldedClient.ListDraftPurchaseIdsAsync instead.
    public string Currency { get; init; } = "eur";
    public IReadOnlyList<string> Tags { get; init; } = []; // doc-level tags
    public IReadOnlyList<HoldedPurchaseLineDto> Lines { get; init; } = [];
}
