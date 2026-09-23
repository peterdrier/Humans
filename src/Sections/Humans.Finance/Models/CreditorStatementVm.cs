using Humans.Finance.Contracts;

namespace Humans.Finance.Models;

/// <summary>Presentation data for a creditor statement. The service's positive owed amount is
/// preserved rather than reinterpreting Holded's signed ledger balance in Razor.</summary>
internal sealed record CreditorStatementVm(
    HoldedCreditorLedger Ledger,
    IReadOnlyList<CreditorLedgerLine> Lines)
{
    public decimal OwedBalance => Ledger.OwedToMember;
}
