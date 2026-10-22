using Microsoft.AspNetCore.Authorization;

namespace Humans.Expenses.Authorization;

/// <summary>
/// The resource is an <c>ExpenseReportDto</c>. Callers outside the section reach <c>View</c> through the
/// <c>PolicyNames.ExpenseReportView</c> policy, never this type.
/// </summary>
internal sealed class ExpenseReportOperationRequirement(ExpenseReportOperation operation) : IAuthorizationRequirement
{
    public ExpenseReportOperation Operation { get; } = operation;
}
