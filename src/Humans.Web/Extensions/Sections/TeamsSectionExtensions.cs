using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Teams;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using TeamsTeamPageService = Humans.Application.Services.Teams.TeamPageService;

namespace Humans.Web.Extensions.Sections;

internal static class TeamsSectionExtensions
{
    internal static IServiceCollection AddTeamsSection(this IServiceCollection services)
    {
        // Contributor-bearing services: register the concrete type once, then
        // forward both the primary interface and IUserDataContributor to that
        // single scoped instance so IGdprExportService can resolve every slice
        // via IEnumerable<IUserDataContributor>.
        services.AddScoped<TeamService>();
        services.AddScoped<ITeamService>(sp => sp.GetRequiredService<TeamService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TeamService>());

        services.AddScoped<ITeamPageService, TeamsTeamPageService>();

        services.AddScoped<ISystemTeamSync, SystemTeamSyncJob>();

        return services;
    }
}
