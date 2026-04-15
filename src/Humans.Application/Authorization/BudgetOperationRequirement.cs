using Microsoft.AspNetCore.Authorization;

namespace Humans.Application.Authorization;

/// <summary>
/// Resource-based authorization requirement for budget operations.
///
/// <list type="bullet">
///   <item>
///     <description>
///       <c>Edit</c> is resource-scoped — pair it with a <c>BudgetCategory</c> to
///       authorize line-item create/update/delete. FinanceAdmin/Admin can edit any
///       category; department coordinators can edit categories linked to their team.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>Manage</c> is global — used for budget-wide admin operations
///       (budget year lifecycle, group/category management, projection parameters,
///       background sync jobs). Only FinanceAdmin/Admin and the system principal succeed.
///     </description>
///   </item>
/// </list>
/// </summary>
public sealed class BudgetOperationRequirement : IAuthorizationRequirement
{
    public static readonly BudgetOperationRequirement Edit = new(nameof(Edit));
    public static readonly BudgetOperationRequirement Manage = new(nameof(Manage));

    public string OperationName { get; }

    private BudgetOperationRequirement(string operationName)
    {
        OperationName = operationName;
    }
}
