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
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Auth;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationSourceContributor, AuthNotificationSources>();
        services.AddSectionDbContext<AuthDbContext>(sentinelTable: "role_assignments");

        // Composed from every section's ISectionRoles (registered by discovery); throws on a duplicate name.
        services.AddSingleton(sp => new RoleCatalog(sp.GetServices<ISectionRoles>()));

        services.AddSingleton<IRoleAssignmentRepository, RoleAssignmentRepository>();
        services.AddScoped<IAdminAuthorizationService, AdminAuthorizationService>();

        // IUserDataContributor and IUserMerge reach the undecorated inner through the unkeyed concrete, which
        // forwards to this keyed registration, so there is only one inner instance.
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
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<CachingRoleAssignmentService>());
        services.AddHostedService(sp => sp.GetRequiredService<CachingRoleAssignmentService>());

        services.AddSingleton<IAuthorizationHandler, RoleAssignmentAuthorizationHandler>();

        services.AddScoped<IMagicLinkUrlBuilder, MagicLinkUrlBuilder>();
        services.AddScoped<IMagicLinkRateLimiter, MagicLinkRateLimiter>();
        services.AddScoped<IMagicLinkService, MagicLinkService>();

        services.AddScoped<AuthEmails>();
        services.AddScoped<IEmailPreviewContributor, AuthEmailPreviews>();

        services.AddSingleton<AuthMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<AuthMetricsService>());
    }
}
