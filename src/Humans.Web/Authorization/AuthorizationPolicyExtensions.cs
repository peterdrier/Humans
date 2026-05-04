using Humans.Application.Authorization;
using Humans.Domain.Constants;
using Humans.Web.Authorization.Handlers;
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
        services.AddScoped<IAuthorizationHandler, AgentRateLimitHandler>();
        services.AddScoped<IAuthorizationHandler, BudgetAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, CampAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, TeamAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, IsAnyTeamManagerOrCoordinatorHandler>();
        services.AddSingleton<IAuthorizationHandler, IssuesAuthorizationHandler>();

        // Service-layer enforcement handlers (singleton — no scoped dependencies)
        services.AddSingleton<IAuthorizationHandler, RoleAssignmentAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, UserEmailAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            // Simple role-based policies
            options.AddPolicy(PolicyNames.AdminOnly, policy =>
                policy.RequireRole(RoleNames.Admin));

            // AnyAdminRole gates the admin-shell entry point (/Admin). The 11 roles
            // mirror the composite OR-chain in _Layout.cshtml that decides whether
            // to show the "Admin" top-nav link. Sidebar items inside /Admin are
            // filtered per-item, so each role only sees what they can act on.
            options.AddPolicy(PolicyNames.AnyAdminRole, policy =>
                policy.RequireRole(
                    RoleNames.Admin,
                    RoleNames.Board,
                    RoleNames.HumanAdmin,
                    RoleNames.TeamsAdmin,
                    RoleNames.CampAdmin,
                    RoleNames.TicketAdmin,
                    RoleNames.FeedbackAdmin,
                    RoleNames.FinanceAdmin,
                    RoleNames.NoInfoAdmin,
                    RoleNames.VolunteerCoordinator,
                    RoleNames.ConsentCoordinator));

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

            // ShiftDashboardAccess stays narrow — gates the privileged sub-panels on the
            // dashboard (coordinator activity, pending shifts, voluntell action). Only the
            // role-based admins / volunteer coordinators see those.
            options.AddPolicy(PolicyNames.ShiftDashboardAccess, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator));

            // ShiftDepartmentManager is wider: privileged dashboard roles OR anyone who is
            // a coordinator / manager of any team or sub-team. Gates the dashboard page
            // entry point and the "open dashboard" button on /Shifts. The role-OR-team-coord
            // disjunction is encoded inside IsAnyTeamManagerOrCoordinatorHandler so the policy
            // stays a single requirement (multiple requirements on a policy AND together).
            options.AddPolicy(PolicyNames.ShiftDepartmentManager, policy =>
                policy.AddRequirements(new IsAnyTeamManagerOrCoordinatorRequirement()));

            options.AddPolicy(PolicyNames.PrivilegedSignupApprover, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin));

            options.AddPolicy(PolicyNames.VolunteerManager, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator));

            options.AddPolicy(PolicyNames.MedicalDataViewer, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin));

            // Agent rate-limit policy
            options.AddPolicy(PolicyNames.AgentRateLimit, policy =>
                policy.AddRequirements(new AgentRateLimitRequirement()));

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
