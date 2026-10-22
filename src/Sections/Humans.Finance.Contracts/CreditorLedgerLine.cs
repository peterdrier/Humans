using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>Credit = owed to the member, debit = paid out. Read from the cached ledger, not live Holded.</summary>
/// <remarks>
/// Deliberately not the Holded connector's <c>HoldedLedgerLineDto</c>, which it mirrors: re-exporting it would make
/// every reader of a creditor statement a consumer of the Holded API.
/// </remarks>
public sealed record CreditorLedgerLine
{
    public required int EntryNumber { get; init; }
    public required int Line { get; init; }
    public required Instant Date { get; init; }
    public required int AccountNum { get; init; }
    public required decimal Debit { get; init; }
    public required decimal Credit { get; init; }
    public string? Type { get; init; }
    public string? Description { get; init; }

    /// <summary>Owed balance (positive = owed to the member) after this line, accumulated oldest first
    /// (date, entry, line) from the account's first line.</summary>
    public decimal RunningBalance { get; init; }
}
