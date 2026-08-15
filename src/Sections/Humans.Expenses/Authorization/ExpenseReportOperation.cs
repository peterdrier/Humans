using Humans.Expenses.Models;
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
    CategoryOverride,
    /// <summary>Re-queue a stuck Holded push. Finance-admin only, and only after approval.</summary>
    RequeueHoldedPush
}
