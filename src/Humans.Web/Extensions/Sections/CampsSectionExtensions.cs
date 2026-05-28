using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Caching;
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
        // Camps section — see #542 + T-06.
        services.AddSingleton<ICampRepository, CampRepository>();

        // Keyed-Scoped inner + Singleton decorator.
        services.AddScoped<CampsCampService>();
        services.AddKeyedScoped<ICampService>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampsCampService>());
        services.AddKeyedScoped<ICampRoleCampAccess>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampsCampService>());
        services.AddKeyedScoped<IUserMerge>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampsCampService>());
        // IUserDataContributor on the inner — matches User/Teams pattern (GDPR export iterates contributors).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampsCampService>());

        // Owns CampInfo + CampSettingsInfo projection; invalidates after every write through this surface.
        services.AddSingleton<CachingCampService>();
        services.AddSingleton<ICampService>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampServiceRead>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampRoleCampAccess>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<IEarlyEntryProvider>(sp => sp.GetRequiredService<CachingCampService>());

        // §15e CRITICAL: same Singleton instance for invalidator + merge as ICampService — one cache, one signaller.
        services.AddSingleton<ICampInfoInvalidator>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<IUserMerge>(sp => sp.GetRequiredService<CachingCampService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCampService>());

        services.AddHostedService(sp => sp.GetRequiredService<CachingCampService>());

        // CampRoleService — separate sub-service, no decorator.
        // Registered under both ICampRoleService and IGoogleGroupMembershipSource so
        // the Google sync orchestrator can enumerate the Camps claim alongside the
        // Teams claim. Mirrors the Teams pattern in TeamsSectionExtensions.
        services.AddScoped<CampsCampRoleService>();
        services.AddScoped<ICampRoleService>(sp => sp.GetRequiredService<CampsCampRoleService>());
        services.AddScoped<IGoogleGroupMembershipSource>(sp => sp.GetRequiredService<CampsCampRoleService>());
        // Lazy<ICampRoleService> resolves a circular dep: CampService -> ICampRoleService -> ICampRoleCampAccess.
        services.AddTransient(sp => new Lazy<ICampRoleService>(sp.GetRequiredService<ICampRoleService>));

        services.AddScoped<ICampContactService, CampsCampContactService>();
        services.AddScoped<CampAdminPageBuilder>();
        services.AddScoped<CampCsvExportBuilder>();

        services.AddScoped<ICampLeadJoinRequestsBadgeCacheInvalidator, CampLeadJoinRequestsBadgeCacheInvalidator>();

        return services;
    }
}
