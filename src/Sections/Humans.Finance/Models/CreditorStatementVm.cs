using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;

namespace Humans.Finance.Models;

/// <summary>Flipped from Holded's sign: positive means the organisation owes the member. <see cref="ExpenseReports"/>
/// are the reports booked to this account, to check the ledger against.</summary>
internal sealed record CreditorStatementVm(
    HoldedCreditorLedger Ledger,
    IReadOnlyList<CreditorStatementLineVm> Lines,
    IReadOnlyList<ExpenseReportDto> ExpenseReports)
{
    public decimal OwedBalance => -Ledger.Balance;
}

/// <summary><paramref name="RunningBalance"/> is owed to the member after this line, from the account's first
/// line.</summary>
internal sealed record CreditorStatementLineVm(CreditorLedgerLine Line, decimal RunningBalance, string? Description);
