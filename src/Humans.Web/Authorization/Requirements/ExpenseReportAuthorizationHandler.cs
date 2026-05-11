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
public sealed class ExpenseReportAuthorizationHandler
    : AuthorizationHandler<ExpenseReportOperationRequirement, ExpenseReportDto>
{
    private readonly IBudgetService _budgetService;
    private readonly ITeamService _teamService;

    public ExpenseReportAuthorizationHandler(IBudgetService budgetService, ITeamService teamService)
    {
        _budgetService = budgetService;
        _teamService = teamService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ExpenseReportOperationRequirement requirement,
        ExpenseReportDto resource)
    {
        var userId = GetUserId(context.User);
        if (userId is null)
            return;

        var op = requirement.Operation;
        var isFinanceAdmin = RoleChecks.IsFinanceAdmin(context.User); // includes Admin
        var isSubmitter = resource.SubmitterUserId == userId.Value;

        // ─── FinanceAdmin + Admin: broad access ────────────────────────────────
        // Per docs/sections/Expenses.md actors table, FinanceAdmin has all coordinator
        // capabilities (Endorse, CoordinatorReject — gated to Submitted) plus the
        // finance-only operations. Admin is a superset of FinanceAdmin.
        if (isFinanceAdmin)
        {
            if (op is ExpenseReportOperation.View
                    or ExpenseReportOperation.Approve
                    or ExpenseReportOperation.FinanceReject
                    or ExpenseReportOperation.CategoryOverride
                    or ExpenseReportOperation.IncludeInSepaPayout)
            {
                context.Succeed(requirement);
                return;
            }

            if (op is ExpenseReportOperation.Endorse or ExpenseReportOperation.CoordinatorReject
                && resource.Status == ExpenseReportStatus.Submitted)
            {
                context.Succeed(requirement);
                return;
            }
        }

        // ─── Submitter rules ───────────────────────────────────────────────────
        if (isSubmitter)
        {
            if (op == ExpenseReportOperation.View)
            {
                context.Succeed(requirement);
                return;
            }

            // Submitter can only edit (header + lines) while in Draft.
            // Lines are frozen at submission — ExpenseReportService.RequireEditableReportAsync
            // enforces this, and the Edit GET/POST controllers also restrict to Draft.
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

            // Submitter can withdraw from any non-terminal post-Draft status.
            // Per docs/sections/Expenses.md invariant: terminal alternates include
            // Withdrawn from Submitted/CoordinatorEndorsed/Approved.
            if (op == ExpenseReportOperation.Withdraw &&
                resource.Status is ExpenseReportStatus.Submitted
                    or ExpenseReportStatus.CoordinatorEndorsed
                    or ExpenseReportStatus.Approved)
            {
                context.Succeed(requirement);
                return;
            }
        }

        // ─── Coordinator checks (require async lookups) ────────────────────────
        // Only relevant for Endorse, CoordinatorReject, or View
        if (op is ExpenseReportOperation.Endorse
                or ExpenseReportOperation.CoordinatorReject
                or ExpenseReportOperation.View)
        {
            if (await IsCoordinatorOfReportCategoryAsync(userId.Value, resource))
            {
                // Coordinator may view any report in their category
                if (op == ExpenseReportOperation.View)
                {
                    context.Succeed(requirement);
                    return;
                }

                // Coordinator may endorse/reject only while in Submitted status
                if (op is ExpenseReportOperation.Endorse or ExpenseReportOperation.CoordinatorReject
                    && resource.Status == ExpenseReportStatus.Submitted)
                {
                    context.Succeed(requirement);
                }
            }
        }
    }

    /// <summary>
    /// Returns true iff the user is a coordinator of the team that owns the
    /// report's budget category. Returns false if the category has no team,
    /// or if the team lookup fails.
    /// </summary>
    private async Task<bool> IsCoordinatorOfReportCategoryAsync(Guid userId, ExpenseReportDto report)
    {
        var category = await _budgetService.GetCategoryByIdAsync(report.BudgetCategoryId);
        if (category?.TeamId is null)
            return false;

        return await _teamService.IsUserCoordinatorOfTeamAsync(category.TeamId.Value, userId);
    }

    private static Guid? GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier);
        if (claim is null) return null;
        return Guid.TryParse(claim.Value, out var id) ? id : null;
    }
}
