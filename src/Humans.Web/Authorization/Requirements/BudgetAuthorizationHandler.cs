using System.Security.Claims;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for budget operations.
/// Evaluates whether a user can perform budget operations on a specific BudgetCategory.
///
/// Authorization logic:
/// - Admin: allow any category
/// - FinanceAdmin: allow any category
/// - Department coordinator: allow only categories linked to their department
/// - Everyone else: deny
///
/// Also denies edits on restricted groups and deleted budget years for non-admin users.
/// </summary>
public class BudgetAuthorizationHandler : AuthorizationHandler<BudgetOperationRequirement, BudgetCategory>
{
    private readonly IBudgetService _budgetService;

    public BudgetAuthorizationHandler(IBudgetService budgetService)
    {
        _budgetService = budgetService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BudgetOperationRequirement requirement,
        BudgetCategory resource)
    {
        // Admin and FinanceAdmin can edit any budget category
        if (RoleChecks.IsFinanceAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        // Non-admin: deny on deleted budget years
        if (resource.BudgetGroup?.BudgetYear?.IsDeleted == true)
            return;

        // Non-admin: deny on restricted groups
        if (resource.BudgetGroup?.IsRestricted == true)
            return;

        // Category must be linked to a team for coordinator-based access
        if (!resource.TeamId.HasValue)
            return;

        // Check if user is a coordinator for the category's department
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !Guid.TryParse(userIdClaim.Value, out var userId))
            return;

        var coordinatorTeamIds = await _budgetService.GetEffectiveCoordinatorTeamIdsAsync(userId);
        if (coordinatorTeamIds.Contains(resource.TeamId.Value))
        {
            context.Succeed(requirement);
        }
    }
}
