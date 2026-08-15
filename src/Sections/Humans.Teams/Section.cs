using Humans.GoogleIntegration.Contracts;
using Humans.Application.Interfaces;
using Humans.Teams.Authorization;
using Microsoft.AspNetCore.Authorization;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.EarlyEntry.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Gdpr.Contracts;
using Humans.Infrastructure.Hosting;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Teams;

/// <summary>
/// Teams' DI entry point, at the project root by convention. Discovered by Shell — nothing
/// names it, so it needs no section prefix. The §15 caching decorator is a Singleton that warms
/// the whole team graph at startup and mutates it in place on write; the keyed inner service is
/// Scoped because it owns the repository's unit of work.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<TeamsDbContext>(sentinelTable: "teams");

        services.AddSingleton<ITeamRepository, TeamRepository>();

        // Keyed-Scoped inner + Singleton decorator.
        services.AddKeyedScoped<ITeamManagementService, TeamService>(CachingTeamService.InnerServiceKey);
        services.AddKeyedScoped<IUserMerge, TeamService>(CachingTeamService.InnerServiceKey);
        services.AddScoped<TeamService>(sp =>
            (TeamService)sp.GetRequiredKeyedService<ITeamManagementService>(CachingTeamService.InnerServiceKey));
        services.AddScoped<IGoogleGroupMembershipSource>(sp => sp.GetRequiredService<TeamService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TeamService>());
        services.AddScoped<IEarlyEntryProvider>(sp => sp.GetRequiredService<TeamService>());

        services.AddSingleton<CachingTeamService>();
        services.AddSingleton<ITeamManagementService>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamService>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamServiceRead>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamSeeding>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<IUserMerge>(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddHostedService(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddScoped<ITeamPageService, TeamPageService>();

        // The one invalidator in the MemoryCacheInvalidators family that is Teams' own: its six
        // siblings wrap IMemoryCache and moved to Base at G5 lane 3a-1, but this one injects
        // ITeamService, so the implementation lives here (and HUM0034 makes it internal, which
        // is why Web can no longer register it). The interface stays on Humans.Teams.Contracts
        // because Humans.Users evicts through it.
        services.AddScoped<IActiveTeamsCacheInvalidator, ActiveTeamsCacheInvalidator>();

        // The system-team reconciler. Its interface stays in Humans.Application because
        // Hangfire serializes ISystemTeamSync as the "system-team-sync" recurring job's
        // target type; the implementation is resolved from DI at execution time (lane 4b-2e).
        services.AddScoped<ISystemTeamSync, SystemTeamSyncJob>();

        // Resource-based handler; the policies it backs stay in Shell's
        // AuthorizationPolicyExtensions (design §15 step 6's asymmetry).
        services.AddScoped<IAuthorizationHandler, TeamAuthorizationHandler>();
    }
}
