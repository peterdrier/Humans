using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.Camps.Authorization;
using Humans.CityPlanning.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
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
public sealed class Section : ISection, IIssueQueueOwner
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<CampsDbContext>(sentinelTable: "camps");

        services.AddSingleton<ICampRepository, CampRepository>();

        services.AddScoped<CampService>();
        services.AddKeyedScoped<ICampManagement>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampService>());
        services.AddKeyedScoped<IUserMerge>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampService>());

        // Owns CampInfo + CampSettingsInfo projection; invalidates after every write through this surface.
        services.AddSingleton<CachingCampService>();
        services.AddSingleton<ICampManagement>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampService>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampLeadDirectory>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampSeeding>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampRoleCampAccess>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<IEarlyEntryProvider>(sp => sp.GetRequiredService<CachingCampService>());

        // Merge, GDPR erasure and role writes change rows the projection serves, so the
        // decorator carries them and drops the projection itself after the inner write.
        services.AddSingleton<IUserMerge>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingCampService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingCampService>());

        services.AddHostedService(sp => sp.GetRequiredService<CachingCampService>());

        // CampRoleService — separate sub-service; ICampRoleService resolves to the
        // decorator, which evicts the projection after role writes.
        // Also registered as IGoogleGroupMembershipSource so the Google sync
        // orchestrator can enumerate the Camps claim alongside the Teams claim.
        // Mirrors the Teams pattern in Humans.Teams' Section.
        services.AddScoped<CampRoleService>();
        services.AddKeyedScoped<ICampRoleService>(
            CachingCampService.InnerServiceKey,
            (sp, _) => sp.GetRequiredService<CampRoleService>());
        services.AddSingleton<ICampRoleService>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddScoped<ICampRoleSeeding>(sp => sp.GetRequiredService<CampRoleService>());
        services.AddScoped<IGoogleGroupMembershipSource>(sp => sp.GetRequiredService<CampRoleService>());
        // Lazy<ICampRoleService> resolves a circular dep: CampService -> ICampRoleService -> ICampRoleCampAccess.
        services.AddTransient(sp => new Lazy<ICampRoleService>(sp.GetRequiredService<ICampRoleService>));
        // Lazy<ICityPlanningWrite> resolves a circular dep: CampService -> ICityPlanningWrite
        // -> ICampService. Used on the camp-delete path to clear the section's polygons.
        services.AddTransient(sp => new Lazy<ICityPlanningWrite>(sp.GetRequiredService<ICityPlanningWrite>));

        services.AddScoped<ICampContactService, CampContactService>();
        services.AddScoped<CampAdminPageBuilder>();
        services.AddScoped<CampCsvExportBuilder>();

        // Resource-based handler; the policies it backs stay in Shell's
        // AuthorizationPolicyExtensions.
        services.AddScoped<IAuthorizationHandler, CampAuthorizationHandler>();

        // Backs CampComplianceAccess, registered by this section's SectionPolicies
        // (nobodies-collective/Humans#1091).
        services.AddScoped<IAuthorizationHandler, CampComplianceAccessHandler>();
    }

    // This section owns the issue queue its members' reports land in; Issues discovers
    // the seam rather than holding a list of sections (memory/architecture/section-contribution-seams.md).
    string IIssueQueueOwner.QueueKey => "Camps";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Camps";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["camps", "barrios"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [RoleNames.CampAdmin];
}
