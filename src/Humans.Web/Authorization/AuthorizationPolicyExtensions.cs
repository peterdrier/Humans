using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Humans.Base.Constants;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization;

/// <summary>
/// Registers the cross-section authorization policies; every other policy is registered by its owning section's
/// <c>ISectionPolicies</c>. Each one is an entry in <c>docs/authorization-inventory.md</c>.
/// </summary>
public static class AuthorizationPolicyExtensions
{
    public static IServiceCollection AddHumansAuthorizationPolicies(this IServiceCollection services)
    {
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
                    RoleNames.RideshareAdmin,
                    RoleNames.NoInfoAdmin,
                    RoleNames.VolunteerCoordinator,
                    RoleNames.ConsentCoordinator));

            options.AddPolicy(PolicyNames.BoardOnly, policy =>
                policy.RequireRole(RoleNames.Board));

            options.AddPolicy(PolicyNames.BoardOrAdmin, policy =>
                policy.RequireRole(RoleNames.Board, RoleNames.Admin));

            // Ticket-admin roles OR the shared gate-terminal account (by well-known
            // id — it holds no roles). The OR lives in one assertion so the policy
            // stays a single requirement (policy requirements AND together).
            // Composite spanning Tickets (TicketAdmin) and Gate (terminal identity).
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
            // the read-only Scanner gate (and the two can diverge later). Composite
            // spanning Tickets (TicketAdmin) and Gate (terminal identity).
            options.AddPolicy(PolicyNames.GateAdmit, policy =>
                policy.RequireAssertion(ctx =>
                    ctx.User.IsInRole(RoleNames.TicketAdmin)
                    || ctx.User.IsInRole(RoleNames.Board)
                    || ctx.User.IsInRole(RoleNames.Admin)
                    || ctx.User.HasClaim(System.Security.Claims.ClaimTypes.NameIdentifier,
                        SystemUserIds.GateTerminal.ToString())));

            // Composite spanning Consent (ConsentCoordinator) and Shifts (VolunteerCoordinator) roles.
            options.AddPolicy(PolicyNames.ReviewQueueAccess, policy =>
                policy.RequireRole(RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator,
                    RoleNames.Board, RoleNames.Admin));

            // Single nav-visibility gate: only Active users see app navigation.
            options.AddPolicy(PolicyNames.AppAccess, policy =>
                policy.RequireAssertion(ctx =>
                    RoleAssignmentClaimsTransformation.IsActive(ctx.User)));

            // Resource-based (the resource is the target role-name string). Registered here so Humans.Users reaches
            // the gate through the policy name alone, and Auth's Contracts leaf never carries an IAuthorizationRequirement.
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
