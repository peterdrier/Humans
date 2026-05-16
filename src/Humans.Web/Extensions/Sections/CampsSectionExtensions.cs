using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Camps;
using Humans.Infrastructure.Services.Camps;
using Humans.Web.Models.CampAdmin;
using CampsCampContactService = Humans.Application.Services.Camps.CampContactService;
using CampsCampRoleService = Humans.Application.Services.Camps.CampRoleService;
using CampsCampService = Humans.Application.Services.Camps.CampService;

namespace Humans.Web.Extensions.Sections;

internal static class CampsSectionExtensions
{
    internal static IServiceCollection AddCampsSection(this IServiceCollection services)
    {
        // Camps section — §15 repository pattern (issue #542) + T-06 caching
        // decorator (2026-05-16 cache-migration plan).
        services.AddSingleton<ICampRepository, CampRepository>();

        // Inner CampService: Scoped + keyed under CachingCampService.InnerServiceKey
        // so the singleton decorator can resolve it per-call without self-
        // resolving the unkeyed ICampService registration.
        services.AddKeyedScoped<ICampService, CampsCampService>(CachingCampService.InnerServiceKey);
        services.AddKeyedScoped<IUserMerge, CampsCampService>(CachingCampService.InnerServiceKey);
        services.AddScoped<CampsCampService>(sp =>
            (CampsCampService)sp.GetRequiredKeyedService<ICampService>(CachingCampService.InnerServiceKey));
        // IUserDataContributor stays on the inner service (Scoped, unkeyed) —
        // GDPR export iterates IEnumerable<IUserDataContributor> and matches
        // the User/Teams pattern. The decorator does not implement the
        // contributor surface.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampsCampService>());

        // CachingCampService: Singleton transparent decorator. Owns the
        // CampInfo + CampSettingsInfo projection and invalidates after every
        // write going through this surface.
        services.AddSingleton<CachingCampService>();
        services.AddSingleton<ICampService>(sp => sp.GetRequiredService<CachingCampService>());

        // ICampInfoInvalidator and IUserMerge must resolve to the SAME
        // singleton instance that backs ICampService (§15e CRITICAL: one
        // cache, one signaller).
        services.AddSingleton<ICampInfoInvalidator>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<IUserMerge>(sp => sp.GetRequiredService<CachingCampService>());

        // Surface CampInfo cache diagnostics on /Admin/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCampService>());

        // CachingCampService is itself the IHostedService — TrackedCache's
        // StartAsync triggers WarmAllAsync when warmOnStartup: true. After a
        // bulk invalidation (Clear), the next read lazily re-warms via
        // EnsureWarmedAsync.
        services.AddHostedService(sp => sp.GetRequiredService<CachingCampService>());

        // CampRoleService — separate sub-service, no decorator.
        services.AddSingleton<ICampRoleRepository, CampRoleRepository>();
        services.AddScoped<ICampRoleService, CampsCampRoleService>();
        // Lazy<ICampRoleService> resolves a circular dep: CampService → ICampRoleService → ICampService.
        services.AddTransient(sp => new Lazy<ICampRoleService>(() => sp.GetRequiredService<ICampRoleService>()));

        services.AddScoped<ICampContactService, CampsCampContactService>();
        services.AddScoped<CampAdminPageBuilder>();
        services.AddScoped<CampCsvExportBuilder>();

        services.AddScoped<ICampLeadJoinRequestsBadgeCacheInvalidator, CampLeadJoinRequestsBadgeCacheInvalidator>();

        return services;
    }
}
