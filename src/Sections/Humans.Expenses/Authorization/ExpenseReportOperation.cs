namespace Humans.Expenses.Authorization;

internal enum ExpenseReportOperation
{
    View,
    Edit,
    Submit,
    Withdraw,
    Endorse,
    CoordinatorReject,
    Approve,
    FinanceReject,
    /// <summary>Finance-admin only, and only after approval.</summary>
    RequeueHoldedPush,
    /// <summary>Whoever may Endorse or Approve, while the report awaits them; never the submitter.</summary>
    DeclineLine
}
