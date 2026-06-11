using System.Security.Claims;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for expense report operations.
/// Encodes the actors-and-roles matrix from the spec (docs/superpowers/specs/2026-05-10-expense-reports-design.md):
/// submitter / coordinator-of-the-report's-category / FinanceAdmin / Admin × operation.
/// Deny-by-default: only explicit Succeed paths grant access.
/// </summary>
public sealed class ExpenseReportAuthorizationHandler(IBudgetService budgetService, ITeamServiceRead teamService)
    : AuthorizationHandler<ExpenseReportOperationRequirement, ExpenseReportDto>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ExpenseReportOperationRequirement requirement,
        ExpenseReportDto resource)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return;

        var op = requirement.Operation;
        var isFinanceAdmin = RoleChecks.IsFinanceAdmin(context.User); // includes Admin
        var isSubmitter = resource.SubmitterUserId == userId;

        if (isFinanceAdmin
            && (op is ExpenseReportOperation.View
                    or ExpenseReportOperation.Approve
                    or ExpenseReportOperation.FinanceReject
                    or ExpenseReportOperation.CategoryOverride
                    or ExpenseReportOperation.IncludeInSepaPayout
                    or ExpenseReportOperation.ReopenSepa
                || (op is ExpenseReportOperation.Endorse or ExpenseReportOperation.CoordinatorReject
                    && resource.Status == ExpenseReportStatus.Submitted)))
        {
            context.Succeed(requirement);
            return;
        }

        if (isSubmitter)
        {
            if (op == ExpenseReportOperation.View)
            {
                context.Succeed(requirement);
                return;
            }

            if (op == ExpenseReportOperation.Edit &&
                resource.Status == ExpenseReportStatus.Draft)
            {
                context.Succeed(requirement);
                return;
            }

            if (op == ExpenseReportOperation.Submit &&
                resource.Status == ExpenseReportStatus.Draft)
            {
                context.Succeed(requirement);
                return;
            }

            if (op == ExpenseReportOperation.Withdraw &&
                resource.Status is ExpenseReportStatus.Submitted
                    or ExpenseReportStatus.CoordinatorEndorsed
                    or ExpenseReportStatus.Approved)
            {
                context.Succeed(requirement);
                return;
            }
        }

        if ((op is ExpenseReportOperation.Endorse
                or ExpenseReportOperation.CoordinatorReject
                or ExpenseReportOperation.View)
            && await IsCoordinatorOfReportCategoryAsync(userId, resource)
            && (op == ExpenseReportOperation.View || resource.Status == ExpenseReportStatus.Submitted))
        {
            context.Succeed(requirement);
        }
    }

    private async Task<bool> IsCoordinatorOfReportCategoryAsync(Guid userId, ExpenseReportDto report)
    {
        var category = await budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
        if (category?.TeamId is null)
            return false;

        var teamsById = await teamService.GetTeamsAsync();
        return TeamCoordinatorAccess.IsCoordinatorOfActiveTeam(teamsById, category.TeamId.Value, userId);
    }
}
