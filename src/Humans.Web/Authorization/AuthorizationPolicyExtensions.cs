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
        services.AddSingleton<IAuthorizationHandler, ActiveMemberOrShiftAccessHandler>();
        services.AddSingleton<IAuthorizationHandler, IsActiveMemberHandler>();
        services.AddSingleton<IAuthorizationHandler, HumanAdminOnlyHandler>();

        // Scoped: depend on scoped services.
        services.AddScoped<IAuthorizationHandler, AgentRateLimitHandler>();
        services.AddScoped<IAuthorizationHandler, BudgetAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, CampAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, ContainerAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, IsAnyTeamManagerOrCoordinatorHandler>();
        services.AddScoped<IAuthorizationHandler, StoreOrderAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, TeamAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, IssuesAuthorizationHandler>();

        services.AddScoped<IAuthorizationHandler, ExpenseReportAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, IbanAccessHandler>();

        services.AddSingleton<IAuthorizationHandler, RoleAssignmentAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, UserEmailAuthorizationHandler>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyNames.AdminOnly, policy =>
                policy.RequireRole(RoleNames.Admin));

            // Mirrors _Layout.cshtml top-nav check; sidebar items are filtered per-item.
            options.AddPolicy(PolicyNames.AnyAdminRole, policy =>
                policy.RequireRole(
                    RoleNames.Admin,
                    RoleNames.Board,
                    RoleNames.HumanAdmin,
                    RoleNames.TeamsAdmin,
                    RoleNames.CampAdmin,
                    RoleNames.TicketAdmin,
                    RoleNames.EventsAdmin,
                    RoleNames.FeedbackAdmin,
                    RoleNames.FinanceAdmin,
                    RoleNames.StoreAdmin,
                    RoleNames.CantinaAdmin,
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

            options.AddPolicy(PolicyNames.EventsAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.EventsAdmin, RoleNames.Admin));

            options.AddPolicy(PolicyNames.CantinaAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.CantinaAdmin, RoleNames.Admin));

            options.AddPolicy(PolicyNames.StoreCatalogAdmin, policy =>
                policy.RequireRole(RoleNames.StoreAdmin, RoleNames.FinanceAdmin, RoleNames.Admin));

            options.AddPolicy(PolicyNames.ReviewQueueAccess, policy =>
                policy.RequireRole(RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator,
                    RoleNames.Board, RoleNames.Admin));

            options.AddPolicy(PolicyNames.ConsentCoordinatorBoardOrAdmin, policy =>
                policy.RequireRole(RoleNames.ConsentCoordinator, RoleNames.Board, RoleNames.Admin));

            // Intentionally identical to ShiftDepartmentManager today; kept separate for future divergence.
            options.AddPolicy(PolicyNames.ShiftDashboardAccess, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator));

            options.AddPolicy(PolicyNames.VolunteerTrackingWrite, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator));

            // Role-OR-team-coord disjunction encoded in IsAnyTeamManagerOrCoordinatorHandler so the policy is one requirement (policy requirements AND).
            options.AddPolicy(PolicyNames.ShiftDepartmentManager, policy =>
                policy.AddRequirements(new IsAnyTeamManagerOrCoordinatorRequirement()));

            options.AddPolicy(PolicyNames.PrivilegedSignupApprover, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin));

            options.AddPolicy(PolicyNames.VolunteerManager, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.VolunteerCoordinator));

            options.AddPolicy(PolicyNames.MedicalDataViewer, policy =>
                policy.RequireRole(RoleNames.Admin, RoleNames.NoInfoAdmin));

            options.AddPolicy(PolicyNames.AgentRateLimit, policy =>
                policy.AddRequirements(new AgentRateLimitRequirement()));

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
