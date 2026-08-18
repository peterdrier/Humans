using Humans.GoogleIntegration.Contracts;
using Humans.Application.Interfaces.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.Camps.Authorization;
using Humans.CityPlanning.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Infrastructure.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Camps;

/// <summary>
/// Camps' DI entry point, at the project root by convention. Discovered by Shell — nothing
/// names it, so it needs no section prefix. The §15 caching decorator is a Singleton owning
/// the canonical per-camp <c>CampInfo</c> projection; the keyed-Scoped inner service owns the
/// repository's unit of work.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<CampsDbContext>(sentinelTable: "camps");

        services.AddSingleton<ICampRepository, CampRepository>();

        // Keyed-Scoped inner + Singleton decorator.
        services.AddScoped<CampService>();
        services.AddKeyedScoped<ICampService>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampService>());
        services.AddKeyedScoped<ICampRoleCampAccess>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampService>());
        services.AddKeyedScoped<IUserMerge>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampService>());
        // IUserDataContributor on the inner — matches User/Teams pattern (GDPR export iterates contributors).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampService>());

        // Owns CampInfo + CampSettingsInfo projection; invalidates after every write through this surface.
        services.AddSingleton<CachingCampService>();
        services.AddSingleton<ICampService>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampServiceRead>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampLeadDirectory>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampSeeding>(sp => sp.GetRequiredService<CachingCampService>());
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
        // Teams claim. Mirrors the Teams pattern in Humans.Teams' Section.
        services.AddScoped<CampRoleService>();
        services.AddScoped<ICampRoleService>(sp => sp.GetRequiredService<CampRoleService>());
        services.AddScoped<ICampRoleSeeding>(sp => sp.GetRequiredService<CampRoleService>());
        services.AddScoped<IGoogleGroupMembershipSource>(sp => sp.GetRequiredService<CampRoleService>());
        // Lazy<ICampRoleService> resolves a circular dep: CampService -> ICampRoleService -> ICampRoleCampAccess.
        services.AddTransient(sp => new Lazy<ICampRoleService>(sp.GetRequiredService<ICampRoleService>));
        // Lazy<ICityPlanningService> resolves a circular dep: CampService -> ICityPlanningService
        // -> ICampServiceRead. Used on the camp-delete path to clear the section's polygons.
        services.AddTransient(sp => new Lazy<ICityPlanningService>(sp.GetRequiredService<ICityPlanningService>));

        services.AddScoped<ICampContactService, CampContactService>();
        services.AddScoped<CampAdminPageBuilder>();
        services.AddScoped<CampCsvExportBuilder>();

        // Resource-based handler; the policies it backs stay in Shell's
        // AuthorizationPolicyExtensions (design §15 step 6's asymmetry).
        services.AddScoped<IAuthorizationHandler, CampAuthorizationHandler>();
    }
}
