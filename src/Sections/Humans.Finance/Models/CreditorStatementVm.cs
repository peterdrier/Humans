using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;

namespace Humans.Finance.Models;

/// <summary>Flipped from Holded's sign: positive means the organisation owes the member. <see cref="ExpenseReports"/>
/// are the reports booked to this account, to check the ledger against.</summary>
internal sealed record CreditorStatementVm(
    CreditorLedger Ledger,
    IReadOnlyList<CreditorStatementLineVm> Lines,
    IReadOnlyList<ExpenseReportDto> ExpenseReports,
    CreditorMatchVm Match)
{
    public decimal OwedBalance => -Ledger.Balance;
}

/// <summary><see cref="RunningBalance"/> is owed to the member after this line, from the account's first
/// line.</summary>
internal sealed record CreditorStatementLineVm(CreditorLedgerLine Line, string? Description)
{
    public decimal RunningBalance => Line.RunningBalance;
}
