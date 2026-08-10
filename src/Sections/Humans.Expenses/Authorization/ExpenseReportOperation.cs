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
    CategoryOverride
}
