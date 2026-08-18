using NodaTime;

namespace Humans.Finance.Contracts;

/// <summary>One journal line of a member's creditor statement: credit = owed to the member,
/// debit = paid out. Sourced from the cached <c>holded_ledger_lines</c> rows, not from a live
/// Holded call.</summary>
/// <remarks>
/// Finance's own boundary type, deliberately not the Holded connector's <c>HoldedLedgerLineDto</c>,
/// which it otherwise mirrors: re-exporting another section's wire shape would make every reader of a
/// creditor statement a consumer of the Holded API. The two drifting apart is the point.
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
}
