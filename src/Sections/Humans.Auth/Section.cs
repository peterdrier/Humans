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
/// The sign-in half is registered by Shell's <c>AuthSectionExtensions</c>, not here.
/// <c>MagicLinkService</c> is a cross-section orchestrator by the hard rules' own
/// definition — it calls no repository, and it injects <c>IEmailService</c> /
/// <c>IEmailMessageFactory</c> from <c>Humans.Email.Contracts</c>, a *vertical* section's
/// leaf. Auth is a horizontal section and <c>peters-hard-rules.md</c> forbids one from
/// referencing a vertical, so the orchestrator, its two Infrastructure collaborators
/// (<c>MagicLinkUrlBuilder</c>, <c>MagicLinkRateLimiter</c>), <c>AccountController</c> and
/// its views all stayed in Base. AuditLog made the same call for
/// <c>AuditViewerService</c> and then <em>reversed</em> it in G5 lane 4b-2h under Peter's
/// 2026-08-14 Base-floor decision, so treat this as interim, not settled: the same question
/// is open here (nobodies-collective/Humans#866).
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
    }
}
