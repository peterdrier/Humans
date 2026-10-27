using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Base.Interfaces;
using Humans.Teams.Authorization;
using Microsoft.AspNetCore.Authorization;
using Humans.Base.Interfaces.Caching;
using Humans.EarlyEntry.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Teams;

public sealed class Section : ISection, IIssueQueueOwner, ISectionRoles
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(TeamsRoles.TeamsAdmin, "Teams Admin",
            "Architects of collaboration. They organize the working groups, provision resources, and make sure every team has what it needs to thrive.",
            "fa-solid fa-people-group"),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<TeamsDbContext>(sentinelTable: "teams");

        services.AddSingleton<ITeamRepository, TeamRepository>();

        services.AddKeyedScoped<ITeamManagementService, TeamService>(CachingTeamService.InnerServiceKey);
        services.AddKeyedScoped<IUserMerge, TeamService>(CachingTeamService.InnerServiceKey);
        services.AddScoped<TeamService>(sp =>
            (TeamService)sp.GetRequiredKeyedService<ITeamManagementService>(CachingTeamService.InnerServiceKey));
        services.AddScoped<IGoogleGroupMembershipSource>(sp => sp.GetRequiredService<TeamService>());
        services.AddScoped<IGoogleDriveAccessSource, TeamDriveAccessSource>();
        services.AddScoped<IEarlyEntryProvider>(sp => sp.GetRequiredService<TeamService>());

        services.AddSingleton<CachingTeamService>();
        services.AddSingleton<ITeamManagementService>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamManagement>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamService>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamWrite>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<ITeamSeeding>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<IUserMerge>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingTeamService>());
        services.AddSingleton<IEntityNameContributor>(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddHostedService(sp => sp.GetRequiredService<CachingTeamService>());

        services.AddScoped<ITeamPageService, TeamPageService>();

        services.AddScoped<ITeamMessageOptionsProvider, TeamMessageOptionsProvider>();

        services.AddScoped<TeamsEmails>();
        services.AddScoped<IEmailPreviewContributor, TeamsEmailPreviews>();

        services.AddScoped<ISystemTeamSync, SystemTeamSyncJob>();

        // The policies this handler backs stay in Shell's AuthorizationPolicyExtensions.
        services.AddScoped<IAuthorizationHandler, TeamAuthorizationHandler>();

        services.AddSingleton<TeamsMetricsService>();
        services.AddHostedService(sp => sp.GetRequiredService<TeamsMetricsService>());
    }

    string IIssueQueueOwner.QueueKey => "Teams";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Teams";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["teams"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [TeamsRoles.TeamsAdmin];
}
