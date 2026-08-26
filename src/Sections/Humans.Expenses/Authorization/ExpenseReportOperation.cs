namespace Humans.Expenses.Authorization;

/// <summary>
/// Operations that can be performed on an expense report.
/// Used with <see cref="ExpenseReportOperationRequirement"/>.
/// </summary>
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
    /// <summary>Re-queue a stuck Holded push. Finance-admin only, and only after approval.</summary>
    RequeueHoldedPush
}
