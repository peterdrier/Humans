using Humans.Finance.Contracts;

namespace Humans.Finance.Models;

/// <summary>Presentation data for a creditor statement, shown from the member's side. The mirror
/// keeps Holded's sign (Σdebit − Σcredit); flipping it here means positive = the organisation owes
/// the member, negative = the member owes the organisation.</summary>
internal sealed record CreditorStatementVm(
    HoldedCreditorLedger Ledger,
    IReadOnlyList<CreditorStatementLineVm> Lines)
{
    public decimal OwedBalance => -Ledger.Balance;
}

/// <summary>One statement row: the journal line plus the owed balance (positive = owed to the
/// member) after it, accumulated from the account's first line.</summary>
internal sealed record CreditorStatementLineVm(CreditorLedgerLine Line, decimal RunningBalance);
