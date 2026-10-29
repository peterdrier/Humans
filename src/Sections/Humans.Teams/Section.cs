using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models;
using Humans.EarlyEntry.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Issues.Contracts;
using Humans.Teams.Authorization;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Services;
using Humans.Teams.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Teams;

public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionRoles,
    ISectionAccessMatrix,
    ISectionAdminTiles,
    ISectionChrome,
    ISectionHelp,
    ISectionJobs,
    ISectionNav,
    ISectionPolicies,
    IUserPart
{
    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(TeamsGalleryViewComponent))];

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(TeamsRoles.TeamsAdmin, "Teams Admin",
            "Architects of collaboration. They organize the working groups, provision resources, and make sure every team has what it needs to thrive.",
            "fa-solid fa-people-group"),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationSourceContributor, TeamsNotificationSources>();
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

    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "Teams",
            SectionName = "Teams",
            Order = 20,
            Roles = ["Volunteer", "Coordinator", "Board", "TeamsAdmin"],
            Features =
            [
                Of("View teams & join", ("Volunteer", Allowed), ("Coordinator", Allowed), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("View team details", ("Volunteer", Allowed), ("Coordinator", Allowed), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("Manage members", ("Volunteer", Denied), ("Coordinator", Allowed), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("Manage roles", ("Volunteer", Denied), ("Coordinator", Allowed), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("Create teams", ("Volunteer", Denied), ("Coordinator", Denied), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("Delete teams", ("Volunteer", Denied), ("Coordinator", Denied), ("Board", Allowed), ("TeamsAdmin", Denied)),
                Of("Google resource sync", ("Volunteer", Denied), ("Coordinator", Denied), ("Board", Denied), ("TeamsAdmin", Limited)),
            ]
        }
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("teams.total", "Teams", "fa-solid fa-people-group", TotalAsync, Weight: 90)
    ];

    private static async ValueTask<AdminTileValue?> TotalAsync(IServiceProvider sp, CancellationToken ct)
    {
        var count = (await sp.GetRequiredService<ITeamService>().GetTeamsAsync(ct)).Count;
        return new AdminTileValue(count.ToString(CultureInfo.CurrentCulture));
    }

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => Entries;

    // Read once: the markdown is embedded in this assembly and never changes at runtime.
    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "Teams", 10),
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        // ISystemTeamSync returns a report, so it can't implement IRecurringJob. Disable it without a redeploy by setting
        // its service mode to "None" in Admin/SyncSettings.
        yield return new RecurringJobDescriptor("teams-system-sync", typeof(ISystemTeamSync), "0 * * * *");
    }

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
        [new("Nav_Teams", Controller: "Team", Action: "Index", Weight: 20)];

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.TeamsAdminBoardOrAdmin, policy =>
            policy.RequireRole(TeamsRoles.TeamsAdmin, RoleNames.Board, RoleNames.Admin));

        // Narrower than TeamsAdminBoardOrAdmin: for gates where Board access would dead-end the viewer (e.g. the Team
        // page's "Open store" link).
        options.AddPolicy(PolicyNames.TeamsAdminOrAdmin, policy =>
            policy.RequireRole(TeamsRoles.TeamsAdmin, RoleNames.Admin));
    }

    IEnumerable<UserPart> IUserPart.Parts() =>
    [
        new(UserPartSlots.Profile, typeof(TeamMessageButtonsViewComponent), Weight: 200),
    ];
}
