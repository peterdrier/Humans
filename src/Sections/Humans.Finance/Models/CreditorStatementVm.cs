using Humans.Finance.Contracts;

namespace Humans.Finance.Models;

/// <summary>Presentation data for a creditor statement, shown from the member's side. The mirror
/// keeps Holded's sign (Σdebit − Σcredit); flipping it here means positive = the organisation owes
/// the member, negative = the member owes the organisation.</summary>
internal sealed record CreditorStatementVm(
    HoldedCreditorLedger Ledger,
    IReadOnlyList<CreditorLedgerLine> Lines)
{
    public decimal OwedBalance => -Ledger.Balance;
}
