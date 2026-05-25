using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Caching;
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
        services.AddSingleton<ITeamRepository, TeamRepository>();

        // Keyed-Scoped inner + Singleton decorator.
        services.AddKeyedScoped<ITeamService, TeamsTeamService>(CachingTeamService.InnerServiceKey);
        services.AddKeyedScoped<IUserMerge, TeamsTeamService>(CachingTeamService.InnerServiceKey);
        services.AddScoped<TeamsTeamService>(sp =>
            (TeamsTeamService)sp.GetRequiredKeyedService<ITeamService>(CachingTeamService.InnerServiceKey));
        services.AddScoped<IGoogleGroupMembershipSource>(sp => sp.GetRequiredService<TeamsTeamService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TeamsTeamService>());

        services.AddSingleton<CachingTeamService>();
        services.AddSingleton<ITeamService>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamServiceRead>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<IUserMerge>(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddHostedService(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddScoped<ITeamPageService, TeamsTeamPageService>();

        // Teams owns the ActiveTeams cache entry; external callers inject the invalidator instead of IMemoryCache.
        services.AddScoped<IActiveTeamsCacheInvalidator, ActiveTeamsCacheInvalidator>();

        services.AddScoped<ISystemTeamSync, SystemTeamSyncJob>();

        return services;
    }
}
