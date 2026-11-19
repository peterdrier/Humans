using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;

namespace Humans.Finance.Models;

/// <summary>Flipped from Holded's sign: positive means the organisation owes the member. <see cref="ExpenseReports"/>
/// are the reports booked to this account, to check the ledger against; <see cref="SepaTransfers"/> are the transfers our
/// SEPA files sent to it, newest first. <see cref="Match"/> is null when the reconciliation is unavailable
/// (<see cref="MatchUnavailable"/> says why) or has nothing on this account.</summary>
internal sealed record CreditorStatementVm(
    CreditorLedger Ledger,
    IReadOnlyList<CreditorStatementLineVm> Lines,
    IReadOnlyList<ExpenseReportDto> ExpenseReports,
    CreditorMatchVm? Match,
    string? MatchUnavailable,
    IReadOnlyList<SepaPayoutTransferRow> SepaTransfers)
{
    public decimal OwedBalance => -Ledger.Balance;
}

/// <summary>One row of the statement: a single ledger line, or one day's payment lines in one direction, which Holded
/// splits per settled purchase although they are one bank transfer. <see cref="RunningBalance"/> is owed to the member
/// after the row, from the account's first line.</summary>
internal sealed record CreditorStatementLineVm(IReadOnlyList<CreditorLedgerLine> Lines, string? Description)
{
    public CreditorStatementLineVm(CreditorLedgerLine line, string? description) : this([line], description) { }

    /// <summary>The row's last line, oldest first, so its running balance is the row's.</summary>
    public CreditorLedgerLine Line => Lines[^1];

    public decimal Credit => Lines.Sum(l => l.Credit);

    public decimal Debit => Lines.Sum(l => l.Debit);

    public decimal RunningBalance => Line.RunningBalance;
}
