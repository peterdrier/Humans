using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Caching;
using Humans.Infrastructure.HostedServices;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Teams;
using Humans.Infrastructure.Services.Teams;
using TeamsTeamPageService = Humans.Application.Services.Teams.TeamPageService;
using TeamsTeamService = Humans.Application.Services.Teams.TeamService;

namespace Humans.Web.Extensions.Sections;

internal static class TeamsSectionExtensions
{
    internal static IServiceCollection AddTeamsSection(this IServiceCollection services)
    {
        // Repository is Singleton (IDbContextFactory-based) — same pattern as
        // every other §15 section.
        services.AddSingleton<ITeamRepository, TeamRepository>();

        // TeamService (inner): Scoped — owns Teams behavior and has scoped
        // cross-section dependencies. Registered under a keyed service so the
        // singleton CachingTeamService decorator can resolve it per call without
        // self-resolving ITeamService.
        services.AddKeyedScoped<ITeamService, TeamsTeamService>(CachingTeamService.InnerServiceKey);
        services.AddKeyedScoped<IUserMerge, TeamsTeamService>(CachingTeamService.InnerServiceKey);
        services.AddScoped<TeamsTeamService>(sp =>
            (TeamsTeamService)sp.GetRequiredKeyedService<ITeamService>(CachingTeamService.InnerServiceKey));
        services.AddScoped<IGoogleGroupMembershipSource>(sp => sp.GetRequiredService<TeamsTeamService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TeamsTeamService>());

        // CachingTeamService: Singleton transparent decorator. The decorator
        // owns the process-local team read model and invalidates it after writes.
        services.AddSingleton<CachingTeamService>();
        services.AddSingleton<ITeamService>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<IUserMerge>(sp => sp.GetRequiredService<CachingTeamService>());

        // Surface TeamInfo cache diagnostics on /Admin/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddHostedService<TeamsWarmupHostedService>();

        services.AddScoped<ITeamPageService, TeamsTeamPageService>();

        // Cross-cutting invalidator for the ActiveTeams cache entry (§15
        // design-rules). Registered alongside the Teams section — Teams
        // owns the cache, external callers (e.g. SystemTeamSyncJob) inject
        // IActiveTeamsCacheInvalidator rather than IMemoryCache.
        services.AddScoped<IActiveTeamsCacheInvalidator, ActiveTeamsCacheInvalidator>();

        services.AddScoped<ISystemTeamSync, SystemTeamSyncJob>();

        return services;
    }
}
