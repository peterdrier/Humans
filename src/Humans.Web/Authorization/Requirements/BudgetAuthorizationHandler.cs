using System.Security.Claims;
using Humans.Application.Authorization;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Resource-based authorization handler for budget operations on a specific
/// <see cref="BudgetCategory"/> (line-item create/update/delete).
///
/// Authorization logic for <see cref="BudgetOperationRequirement.Edit"/>:
/// - Admin / FinanceAdmin / system principal: allow any category
/// - Department coordinator: allow only categories linked to their department
/// - Everyone else: deny
///
/// Also denies edits on restricted groups and deleted budget years for non-admin users.
///
/// Uses <see cref="IServiceProvider"/> to lazily resolve <see cref="IBudgetService"/>,
/// breaking the DI cycle:
/// BudgetService → IAuthorizationService → BudgetAuthorizationHandler → IBudgetService.
/// </summary>
public class BudgetAuthorizationHandler : AuthorizationHandler<BudgetOperationRequirement, BudgetCategory>
{
    private readonly IServiceProvider _serviceProvider;

    public BudgetAuthorizationHandler(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        BudgetOperationRequirement requirement,
        BudgetCategory resource)
    {
        // Only this handler fires for the Edit requirement. Manage is handled by
        // BudgetManageAuthorizationHandler with no resource.
        if (!ReferenceEquals(requirement, BudgetOperationRequirement.Edit))
            return;

        // System principal (background jobs) is always allowed
        if (SystemPrincipal.IsSystem(context.User))
        {
            context.Succeed(requirement);
            return;
        }

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

        var budgetService = _serviceProvider.GetRequiredService<IBudgetService>();
        var coordinatorTeamIds = await budgetService.GetEffectiveCoordinatorTeamIdsAsync(userId);
        if (coordinatorTeamIds.Contains(resource.TeamId.Value))
        {
            context.Succeed(requirement);
        }
    }
}
