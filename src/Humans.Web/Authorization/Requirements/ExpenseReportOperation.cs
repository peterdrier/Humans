namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Operations that can be performed on an expense report.
/// Used with <see cref="ExpenseReportOperationRequirement"/>.
/// </summary>
public enum ExpenseReportOperation
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
    IncludeInSepaPayout
}
