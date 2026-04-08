using Humans.Domain.Constants;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization;

/// <summary>
/// Registers all canonical authorization policies for the Humans application.
/// Each policy corresponds to an entry in the authorization inventory
/// (docs/authorization-inventory.md, Section 5).
/// </summary>
public static class AuthorizationPolicyExtensions
{
    public static IServiceCollection AddHumansAuthorizationPolicies(this IServiceCollection services)
    {
        // Register custom authorization handlers for composite policies
        services.AddSingleton<IAuthorizationHandler, ActiveMemberOrShiftAccessHandler>();
        services.AddSingleton<IAuthorizationHandler, IsActiveMemberHandler>();
        services.AddSingleton<IAuthorizationHandler, HumanAdminOnlyHandler>();

        // Resource-based authorization handlers (scoped — they depend on scoped services)
        services.AddScoped<IAuthorizationHandler, BudgetAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            // Simple role-based policies
            options.AddPolicy(PolicyNames.AdminOnly, policy =>
                policy.RequireRole(RoleNames.Admin));

            options.AddPolicy(PolicyNames.BoardOnly, policy =>
                policy.RequireRole(RoleNames.Board));

            options.AddPolicy(PolicyNames.BoardOrAdmin, policy =>
                policy.RequireRole(RoleNames.Board, RoleNames.Admin));

            options.AddPolicy(PolicyNames.HumanAdminBoardOrAdmin, policy =>
                policy.RequireRole(RoleNames.HumanAdmin, RoleNames.Board, RoleNames.Admin));

            options.AddPolicy(PolicyNames.HumanAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.HumanAdmin, RoleNames.Admin));

            options.AddPolicy(PolicyNames.TeamsAdminBoardOrAdmin, policy =>
                policy.RequireRole(RoleNames.TeamsAdmin, RoleNames.Board, RoleNames.Admin));

            options.AddPolicy(PolicyNames.CampAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.CampAdmin, RoleNames.Admin));

            options.AddPolicy(PolicyNames.TicketAdminBoardOrAdmin, policy =>
                policy.RequireRole(RoleNames.TicketAdmin, RoleNames.Admin, RoleNames.Board));

            options.AddPolicy(PolicyNames.TicketAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.TicketAdmin, RoleNames.Admin));

            options.AddPolicy(PolicyNames.FeedbackAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.FeedbackAdmin, RoleNames.Admin));

            options.AddPolicy(PolicyNames.FinanceAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.FinanceAdmin, RoleNames.Admin));

            options.AddPolicy(PolicyNames.ReviewQueueAccess, policy =>
                policy.RequireRole(RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator,
                    RoleNames.Board, RoleNames.Admin));

            options.AddPolicy(PolicyNames.ConsentCoordinatorBoardOrAdmin, policy =>
                policy.RequireRole(RoleNames.ConsentCoordinator, RoleNames.Board, RoleNames.Admin));

            // ShiftDashboardAccess and ShiftDepartmentManager are intentionally identical today
            // (both map to ShiftRoleChecks.CanManageDepartment). Kept separate so they can
            // diverge when per-department manager roles are introduced.
            options.AddPolicy(PolicyNames.ShiftDashboardAccess, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator));

            options.AddPolicy(PolicyNames.ShiftDepartmentManager, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator));

            options.AddPolicy(PolicyNames.PrivilegedSignupApprover, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin));

            options.AddPolicy(PolicyNames.VolunteerManager, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator));

            options.AddPolicy(PolicyNames.VolunteerSectionAccess, policy =>
                policy.RequireRole(RoleNames.TeamsAdmin, RoleNames.Board, RoleNames.Admin,
                    RoleNames.VolunteerCoordinator));

            options.AddPolicy(PolicyNames.MedicalDataViewer, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin));

            // Composite policies using custom requirements
            options.AddPolicy(PolicyNames.ActiveMemberOrShiftAccess, policy =>
                policy.AddRequirements(new ActiveMemberOrShiftAccessRequirement()));

            options.AddPolicy(PolicyNames.IsActiveMember, policy =>
                policy.AddRequirements(new IsActiveMemberRequirement()));

            options.AddPolicy(PolicyNames.HumanAdminOnly, policy =>
                policy.AddRequirements(new HumanAdminOnlyRequirement()));
        });

        return services;
    }
}
