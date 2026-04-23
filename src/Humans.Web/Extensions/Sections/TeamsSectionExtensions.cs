using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Teams;
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

        // Application-layer TeamService (§15 Part 1 — issue #540a). Scoped so
        // constructor-injected Scoped dependencies (IAuditLogService,
        // INotificationEmitter, IShiftManagementService, invalidators) resolve
        // cleanly. The in-memory active-teams projection lives behind
        // IMemoryCache with a 10-minute TTL — same shape as the pre-migration
        // service, and the precedent set by CampService (§15i Camps entry).
        // Revisiting this section's caching pattern is a separable follow-up:
        // if profiling later shows it matters, a Singleton CachingTeamService
        // decorator can replace the IMemoryCache entry without changing the
        // ITeamService surface.
        //
        // Contributor-bearing: forward both ITeamService and
        // IUserDataContributor to the same scoped instance so GDPR export
        // assembles correctly.
        services.AddScoped<TeamsTeamService>();
        services.AddScoped<ITeamService>(sp => sp.GetRequiredService<TeamsTeamService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TeamsTeamService>());

        services.AddScoped<ITeamPageService, TeamsTeamPageService>();

        services.AddScoped<ISystemTeamSync, SystemTeamSyncJob>();

        return services;
    }
}
