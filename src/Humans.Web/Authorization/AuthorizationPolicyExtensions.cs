using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Humans.Base.Constants;
using Humans.Base.Authorization;
using Humans.Shifts.Contracts;
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
        services.AddSingleton<IAuthorizationHandler, HumanAdminOnlyHandler>();

        // TeamAuthorizationHandler is registered by Humans.Teams' Section.Register: the handler
        // moved into the section at its G5 and is internal there, while the policies it backs
        // stay here (design §15 step 6's asymmetry). CampComplianceAccessHandler and
        // IsAnyTeamManagerOrCoordinatorHandler are registered the same way by Humans.Shifts'
        // Section.Register; their Requirement types live under Shifts' Contracts/ (HUM0034)
        // since this policy wiring constructs them directly.


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

            options.AddPolicy(PolicyNames.TeamsAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.TeamsAdmin, RoleNames.Admin));

            options.AddPolicy(PolicyNames.CampAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.CampAdmin, RoleNames.Admin));

            // CampAdmin/Admin OR any team coordinator — the OR (including the
            // team-coordinator lookup) lives in CampComplianceAccessHandler so the
            // policy is a single requirement (policy requirements AND together).
            options.AddPolicy(PolicyNames.CampComplianceAccess, policy =>
                policy.AddRequirements(new CampComplianceAccessRequirement()));

            options.AddPolicy(PolicyNames.TicketAdminBoardOrAdmin, policy =>
                policy.RequireRole(RoleNames.TicketAdmin, RoleNames.Admin, RoleNames.Board));

            options.AddPolicy(PolicyNames.TicketAdminOrAdmin, policy =>
                policy.RequireRole(RoleNames.TicketAdmin, RoleNames.Admin));

            // Ticket-admin roles OR the shared gate-terminal account (by well-known
            // id — it holds no roles). The OR lives in one assertion so the policy
            // stays a single requirement (policy requirements AND together).
            options.AddPolicy(PolicyNames.ScannerAccess, policy =>
                policy.RequireAssertion(ctx =>
                    ctx.User.IsInRole(RoleNames.TicketAdmin)
                    || ctx.User.IsInRole(RoleNames.Board)
                    || ctx.User.IsInRole(RoleNames.Admin)
                    || ctx.User.HasClaim(System.Security.Claims.ClaimTypes.NameIdentifier,
                        SystemUserIds.GateTerminal.ToString())));

            // Gate write actions (/Gate/Decision, /Gate/Claim POST). Same principals
            // as ScannerAccess today — including the shared gate-terminal account by
            // well-known id — but a separate policy so the write path never rides on
            // the read-only Scanner gate (and the two can diverge later).
            options.AddPolicy(PolicyNames.GateAdmit, policy =>
                policy.RequireAssertion(ctx =>
                    ctx.User.IsInRole(RoleNames.TicketAdmin)
                    || ctx.User.IsInRole(RoleNames.Board)
                    || ctx.User.IsInRole(RoleNames.Admin)
                    || ctx.User.HasClaim(System.Security.Claims.ClaimTypes.NameIdentifier,
                        SystemUserIds.GateTerminal.ToString())));

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

            // Single nav-visibility gate: only Active users see app navigation.
            options.AddPolicy(PolicyNames.AppAccess, policy =>
                policy.RequireAssertion(ctx =>
                    RoleAssignmentClaimsTransformation.IsActive(ctx.User)));

            options.AddPolicy(PolicyNames.HumanAdminOnly, policy =>
                policy.AddRequirements(new HumanAdminOnlyRequirement()));

            // Resource-based (the resource is the target role-name string). Naming the
            // requirement type is policy-registration work and therefore Shell's — it lets
            // Humans.Users reach the gate through the policy name alone, so Auth's Contracts
            // leaf never has to carry an IAuthorizationRequirement. ("Framework-free leaf" was
            // the old wording; G5 lane 3c measured it false — the leaf resolves
            // Microsoft.AspNetCore.App transitively through Humans.Interfaces. Keeping the
            // requirement out is a choice, enforced by
            // AuthArchitectureTests.ContractsLeafNamesNoAspNetType.)
            options.AddPolicy(PolicyNames.RoleAssignmentManage, policy =>
                policy.AddRequirements(RoleAssignmentOperationRequirement.Manage));
        });

        // Sections register their own policies. Configure<AuthorizationOptions> is additive,
        // so cross-section policies keep registering above.
        var contributors = SectionDiscoveryExtensions.DiscoverImplementations<ISectionPolicies>();
        if (contributors.Count > 0)
        {
            services.Configure<AuthorizationOptions>(options =>
            {
                foreach (var contributor in contributors)
                {
                    contributor.AddPolicies(options);
                }
            });
        }

        return services;
    }
}
