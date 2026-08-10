using Microsoft.AspNetCore.Authorization;
using Humans.Expenses.Models;
using Humans.Expenses.Services.Dtos;

namespace Humans.Expenses.Authorization;

/// <summary>
/// Resource-based authorization requirement for expense report operations.
/// Used with IAuthorizationService.AuthorizeAsync(User, reportDto, requirement)
/// where the resource is an <c>ExpenseReportDto</c>.
/// </summary>
internal sealed class ExpenseReportOperationRequirement(ExpenseReportOperation operation) : IAuthorizationRequirement
{
    public ExpenseReportOperation Operation { get; } = operation;
}
