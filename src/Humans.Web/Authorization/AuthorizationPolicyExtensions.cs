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
            // Every endpoint without its own [Authorize]/[AllowAnonymous] (health checks, hubs, minimal APIs,
            // Hangfire) requires a signed-in user; public surfaces opt out with [AllowAnonymous] / .AllowAnonymous().
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

            options.AddPolicy(PolicyNames.AdminOnly, policy =>
                policy.RequireRole(RoleNames.Admin));

            options.AddPolicy(PolicyNames.BoardOnly, policy =>
                policy.RequireRole(RoleNames.Board));

            options.AddPolicy(PolicyNames.BoardOrAdmin, policy =>
                policy.RequireRole(RoleNames.Board, RoleNames.Admin));

            // Single nav-visibility gate: only Active users see app navigation.
            options.AddPolicy(PolicyNames.AppAccess, policy =>
                policy.RequireAssertion(ctx =>
                    RoleAssignmentClaimsTransformation.IsActive(ctx.User)));

            // Resource-based (the resource is the target role-name string). Registered here so Humans.Users reaches
            // the gate through the policy name alone, and Auth's Contracts leaf never carries an IAuthorizationRequirement.
            options.AddPolicy(PolicyNames.RoleAssignmentManage, policy =>
                policy.AddRequirements(RoleAssignmentOperationRequirement.Manage));
        });

        // The admin-shell entry gate (the ADMIN link, /Admin): every catalogued role that opens the shell. Derived
        // from the sections' ISectionRoles so a new role joins without a Shell edit; sidebar items filter per item.
        services.AddOptions<AuthorizationOptions>().Configure<RoleCatalog>((options, catalog) =>
            options.AddPolicy(PolicyNames.AnyAdminRole, policy =>
                policy.RequireRole(catalog.Roles.Where(r => r.OpensAdminShell).Select(r => r.Name).ToArray())));

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
