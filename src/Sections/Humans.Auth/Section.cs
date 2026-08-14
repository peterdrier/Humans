using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Users;
using Humans.Auth.Authorization;
using Humans.Auth.Contracts;
using Humans.Auth.Data;
using Humans.Auth.Services;
using Humans.Gdpr.Contracts;
using Humans.Infrastructure.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Auth;

/// <summary>
/// Auth's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <para>
/// What is registered here is the role-assignment half of the section: the
/// <c>role_assignments</c> repository, the service that owns its invariants, the §15
/// caching decorator over the row set, the full-Admin destructive-action guard, and the
/// resource-based handler that gates who may assign which role.
/// </para>
/// <para>
/// The sign-in half is registered here too, as of nobodies-collective/Humans#866 G5 lane
/// 4b-2i: <c>MagicLinkService</c> plus its two collaborators
/// (<c>MagicLinkUrlBuilder</c>, <c>MagicLinkRateLimiter</c>). It used to live in Base
/// because it injects <c>IEmailService</c> / <c>IEmailMessageFactory</c> from
/// <c>Humans.Email.Contracts</c> — a *vertical* section's leaf, which the old reading of
/// <c>peters-hard-rules.md</c> put out of a horizontal's reach. Peter's Base-floor decision
/// of 2026-08-14 makes a leaf referenceable from anywhere, so that reason dissolved and
/// Auth's own sign-in path came home. Behaviour is unchanged: same lifetimes, same
/// implementations, same order of resolution.
/// </para>
/// <para>
/// <c>AccountController</c> and <c>Views/Account/*</c> did <em>not</em> follow it. They stay
/// in Shell: every action they expose writes Users'/Profiles' tables through those sections'
/// services, and the 35 <c>Login_*</c>/<c>MagicLink*</c>/<c>GateLogin_*</c> resource keys
/// stay in <c>SharedResource</c> with them, so the section still ships no
/// <c>Resources/</c> folder.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<AuthDbContext>(sentinelTable: "role_assignments");

        // §15 repository pattern (issue #551): Singleton + IDbContextFactory (§15b) so the
        // repository owns context lifetime while AuthDbContext itself stays Scoped.
        services.AddSingleton<IRoleAssignmentRepository, RoleAssignmentRepository>();
        services.AddScoped<IAdminAuthorizationService, AdminAuthorizationService>();

        // Issue #749: Inner RoleAssignmentService registered keyed under
        // CachingRoleAssignmentService.InnerServiceKey; the unkeyed concrete forwards to the
        // keyed registration via cast so IUserDataContributor and IUserMerge resolve the same
        // scoped instance the decorator wraps.
        services.AddKeyedScoped<IRoleAssignmentService, RoleAssignmentService>(
            CachingRoleAssignmentService.InnerServiceKey);
        services.AddScoped<RoleAssignmentService>(sp =>
            (RoleAssignmentService)sp.GetRequiredKeyedService<IRoleAssignmentService>(
                CachingRoleAssignmentService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<RoleAssignmentService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<RoleAssignmentService>());

        services.AddSingleton<CachingRoleAssignmentService>();
        services.AddSingleton<IRoleAssignmentService>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddSingleton<IRoleAssignmentCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingRoleAssignmentService>());

        // Resource-based handler moves into the section; the *policy* registration stays in
        // Shell's AuthorizationPolicyExtensions (template step 6's asymmetry).
        services.AddSingleton<IAuthorizationHandler, RoleAssignmentAuthorizationHandler>();

        // The magic-link sign-in path, lifted verbatim out of Shell's AuthSectionExtensions
        // (G5 lane 4b-2i). Scoped, in this order, exactly as before — the sign-in path must
        // resolve identically across the deploy.
        services.AddScoped<IMagicLinkUrlBuilder, MagicLinkUrlBuilder>();
        services.AddScoped<IMagicLinkRateLimiter, MagicLinkRateLimiter>();
        services.AddScoped<IMagicLinkService, MagicLinkService>();
    }
}
