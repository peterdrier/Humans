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
public sealed class IbanAccessHandler(IExpenseReportServiceRead expenseReports)
    : AuthorizationHandler<IbanAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IbanAccessRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return;

        if (userId == requirement.TargetUserId)
        {
            context.Succeed(requirement);
            return;
        }

        if (requirement.IsAdminPageContext && RoleChecks.IsAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        if (requirement.ReportId is { } reportId && RoleChecks.IsFinanceAdmin(context.User))
        {
            var report = await expenseReports.GetAsync(reportId);
            if (report is not null &&
                report.Status is not ExpenseReportStatus.Draft and not ExpenseReportStatus.Withdrawn)
            {
                context.Succeed(requirement);
            }
        }
    }
}
