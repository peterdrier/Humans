using System.Security.Claims;
using Humans.Application.Interfaces.Expenses;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Authorization handler for <see cref="IbanAccessRequirement"/>.
/// Grant rules (deny by default):
/// <list type="bullet">
///   <item><description>Self: requester is the target user.</description></item>
///   <item><description>FinanceAdmin with report context: the report is non-Draft and non-Withdrawn.</description></item>
///   <item><description>Admin on admin page: <see cref="IbanAccessRequirement.IsAdminPageContext"/> is true.</description></item>
/// </list>
/// </summary>
public sealed class IbanAccessHandler : AuthorizationHandler<IbanAccessRequirement>
{
    private readonly IExpenseReportService _expenseService;

    public IbanAccessHandler(IExpenseReportService expenseService)
    {
        _expenseService = expenseService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IbanAccessRequirement requirement)
    {
        var userId = GetUserId(context.User);
        if (userId is null)
            return;

        // Self access: the requester is the IBAN owner
        if (userId.Value == requirement.TargetUserId)
        {
            context.Succeed(requirement);
            return;
        }

        // Admin on admin page (e.g. /Admin/Users/{id} with Reveal IBAN action)
        if (requirement.IsAdminPageContext && RoleChecks.IsAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        // FinanceAdmin in a report context: allowed only if the report exists
        // and is neither Draft nor Withdrawn
        if (requirement.ReportId.HasValue && RoleChecks.IsFinanceAdmin(context.User))
        {
            var report = await _expenseService.GetAsync(requirement.ReportId.Value);
            if (report is not null &&
                report.Status is not ExpenseReportStatus.Draft and not ExpenseReportStatus.Withdrawn)
            {
                context.Succeed(requirement);
            }
        }
    }

    private static Guid? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (claim is null) return null;
        return Guid.TryParse(claim.Value, out var id) ? id : null;
    }
}
