using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Auth.Authorization;
using Humans.Auth.Contracts;
using Humans.Auth.Data;
using Humans.Auth.Services;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Email.Contracts;
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
/// <c>role_assignments</c> repository, the service that owns its invariants, the
/// caching decorator over the row set, the full-Admin destructive-action guard, and the
/// resource-based handler that gates who may assign which role.
/// </para>
/// <para>
/// The sign-in half is registered here too: <c>MagicLinkService</c> plus its two
/// collaborators (<c>MagicLinkUrlBuilder</c>, <c>MagicLinkRateLimiter</c>).
/// </para>
/// <para>
/// <c>AccountController</c> and <c>Views/Account/*</c> stay in Shell: every action they
/// expose writes Users'/Profiles' tables through those sections' services, and their
/// <c>Login_*</c>/<c>MagicLink*</c>/<c>GateLogin_*</c> resource keys stay in
/// <c>SharedResource</c> with them. The section's own <c>AuthResource</c> set at
/// the project root holds only the magic-link email copy.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<AuthDbContext>(sentinelTable: "role_assignments");

        // Singleton + IDbContextFactory so the repository owns context lifetime while
        // AuthDbContext itself stays Scoped.
        services.AddSingleton<IRoleAssignmentRepository, RoleAssignmentRepository>();
        services.AddScoped<IAdminAuthorizationService, AdminAuthorizationService>();

        // Inner RoleAssignmentService registered keyed under
        // CachingRoleAssignmentService.InnerServiceKey; the unkeyed concrete forwards to the
        // keyed registration via cast so IUserDataContributor and IUserMerge reach the
        // undecorated inner through that one registration, rather than each getting a private
        // RoleAssignmentService.
        services.AddKeyedScoped<IRoleAssignmentManagement, RoleAssignmentService>(
            CachingRoleAssignmentService.InnerServiceKey);
        services.AddScoped<RoleAssignmentService>(sp =>
            (RoleAssignmentService)sp.GetRequiredKeyedService<IRoleAssignmentManagement>(
                CachingRoleAssignmentService.InnerServiceKey));
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<RoleAssignmentService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<RoleAssignmentService>());

        services.AddSingleton<CachingRoleAssignmentService>();
        services.AddSingleton<IRoleAssignmentManagement>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddSingleton<IRoleAssignmentService>(sp =>
            sp.GetRequiredService<IRoleAssignmentManagement>());
        services.AddSingleton<IRoleAssignmentWrite>(sp =>
            sp.GetRequiredService<IRoleAssignmentManagement>());
        services.AddSingleton<IRoleAssignmentCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingRoleAssignmentService>());

        // Resource-based handler registers here; its policy is a shared platform policy that
        // stays in Shell's AuthorizationPolicyExtensions.
        services.AddSingleton<IAuthorizationHandler, RoleAssignmentAuthorizationHandler>();

        services.AddScoped<IMagicLinkUrlBuilder, MagicLinkUrlBuilder>();
        services.AddScoped<IMagicLinkRateLimiter, MagicLinkRateLimiter>();
        services.AddScoped<IMagicLinkService, MagicLinkService>();

        // Auth owns its email copy and its gallery samples; Email keeps the mechanics
        // (memory/architecture/email-templates-live-in-sender.md).
        services.AddScoped<AuthEmails>();
        services.AddScoped<IEmailPreviewContributor, AuthEmailPreviews>();

        // Gauge-refresh loop for active role-assignment counts.
        services.AddSingleton<AuthMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<AuthMetricsService>());
    }
}
