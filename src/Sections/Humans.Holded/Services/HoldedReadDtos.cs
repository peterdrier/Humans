using NodaTime;

namespace Humans.Holded.Services;

internal sealed record HoldedExpenseAccountDto
{
    public required string Id { get; init; }
    public required int AccountNum { get; init; }
    public required string Name { get; init; }
}

/// <summary>From GET v2/ledger-entries. <c>account</c> is the 8-digit ledger number, not a Holded id;
/// amounts are euros. <c>date</c> arrives as DD/MM/YYYY and is parsed to Madrid midnight.</summary>
internal sealed record HoldedLedgerLineDto
{
    public required int EntryNumber { get; init; }
    public required int Line { get; init; }
    public required Instant Date { get; init; }        // `date` (dd/MM/yyyy, Madrid midnight)
    public required int AccountNum { get; init; }      // `account`
    public required decimal Debit { get; init; }
    public required decimal Credit { get; init; }
    public string? Type { get; init; }                 // e.g. "purchase", "payment"
    public string? Description { get; init; }
    /// <summary>`id`: the journal entry's id, identical on every line of the entry. Not a line key.</summary>
    public string? EntryId { get; init; }
    /// <summary>`checked`: Holded's manual tick in its ledger view, not bank reconciliation.</summary>
    public bool Checked { get; init; }
}

internal sealed record HoldedAccountDto
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

internal sealed record HoldedUsageDto
{
    public required string Period { get; init; }         // "2026-08"
    public required long Usage { get; init; }
}

