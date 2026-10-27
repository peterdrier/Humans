using Humans.Base.Authorization;
using System.Security.Claims;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Expenses.Authorization;

/// <summary>Deny by default: only explicit Succeed paths grant access.</summary>
internal sealed class ExpenseReportAuthorizationHandler(
    IBudgetService budgetService, ITeamService teamService, IUserService userService, IPreapprovalService preapprovals)
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

        if (isFinanceAdmin
            && (op == ExpenseReportOperation.View
                // A finance decision is open only while the report awaits one (the repository's gate).
                || (op is ExpenseReportOperation.Approve or ExpenseReportOperation.FinanceReject
                    && resource.Status is ExpenseReportStatus.Submitted
                        or ExpenseReportStatus.CoordinatorEndorsed)
                // Filing and fixing a report for a member who cannot. Editing a Submitted or CoordinatorEndorsed
                // report does not send it back a step: the endorsement stands.
                || (op is ExpenseReportOperation.Edit
                    && resource.Status is ExpenseReportStatus.Draft
                        or ExpenseReportStatus.Submitted
                        or ExpenseReportStatus.CoordinatorEndorsed)
                || (op is ExpenseReportOperation.Submit
                    && resource.Status == ExpenseReportStatus.Draft)
                || (op is ExpenseReportOperation.Endorse or ExpenseReportOperation.CoordinatorReject
                    && resource.Status == ExpenseReportStatus.Submitted)
                || (op is ExpenseReportOperation.DeclineLine
                    && resource.Status is ExpenseReportStatus.Submitted
                        or ExpenseReportStatus.CoordinatorEndorsed)
                // The push is queued at approval, so there is nothing to re-queue before it.
                || (op is ExpenseReportOperation.RequeueHoldedPush
                    && resource.Status == ExpenseReportStatus.Approved)))
        {
            context.Succeed(requirement);
            return;
        }

        var isSubmitter = resource.SubmitterUserId == userId
            || (await userService.GetUserInfoAsync(userId))?.AllUserIds.Contains(resource.SubmitterUserId) == true;
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

            // Approval queues the Holded push, so an Approved report is past withdrawing.
            if (op == ExpenseReportOperation.Withdraw &&
                resource.Status is ExpenseReportStatus.Submitted
                    or ExpenseReportStatus.CoordinatorEndorsed)
            {
                context.Succeed(requirement);
                return;
            }
        }

        // A first reviewer holds Endorse only while the report is Submitted, so DeclineLine follows it.
        if ((op is ExpenseReportOperation.Endorse
                or ExpenseReportOperation.CoordinatorReject
                or ExpenseReportOperation.DeclineLine
                or ExpenseReportOperation.View)
            && (op == ExpenseReportOperation.View || resource.Status == ExpenseReportStatus.Submitted)
            && await IsFirstReviewerAsync(userId, resource))
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>
    /// The category's coordinator, or for a pre-approved report (which has no category) a manager of the grant's
    /// program.
    /// </summary>
    private async Task<bool> IsFirstReviewerAsync(Guid userId, ExpenseReportDto report)
    {
        if (report.PreapprovalGrantId is { } grantId)
            return (await preapprovals.ListManagedGrantIdsAsync(userId, CancellationToken.None)).Contains(grantId);

        // No budget category (an account outside the budget) means no coordinator route at all.
        if (report.BudgetCategoryId is not { } categoryId) return false;
        var category = await budgetService.GetCategoryByIdAsync(categoryId);
        if (category?.TeamId is null)
            return false;

        return await teamService.IsUserCoordinatorOfTeamAsync(category.TeamId.Value, userId);
    }
}
