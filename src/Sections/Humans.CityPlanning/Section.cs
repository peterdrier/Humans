using Humans.Base.Authorization;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Models;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Authorization;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Services;
using Humans.CityPlanning.ViewComponents;
using Humans.Issues.Contracts;
using Humans.Settings.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.CityPlanning;

/// <summary>
/// <c>Lazy&lt;ICityPlanningWrite&gt;</c> is registered by Camps' <c>Section</c>, not here: it breaks the
/// CampService ↔ CityPlanningService construction cycle, so it belongs with the consumer.
/// </summary>
public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionAccessMatrix,
    ISectionAdminNav,
    ISectionConfiguration,
    ISectionEndpoints,
    ISectionHelp,
    ISectionNav,
    ISectionPolicies,
    ISectionSettings
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CityPlanningOptions>(configuration.GetSection(CityPlanningOptions.SectionName));

        services.AddSectionDbContext<CityPlanningDbContext>(sentinelTable: "city_planning_settings");

        services.AddSingleton<ICityPlanningRepository, CityPlanningRepository>();
        services.AddScoped<CityPlanningService>();
        services.AddScoped<ICityPlanningWrite>(sp => sp.GetRequiredService<CityPlanningService>());
        services.AddScoped<ICityPlanningService>(sp => sp.GetRequiredService<CityPlanningService>());

        services.AddScoped<IAuthorizationHandler, CityPlanningMapAdminHandler>();
    }

    string IIssueQueueOwner.QueueKey => "CityPlanning";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_CityPlanning";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["cityplanning"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [CampsRoles.CampsAdmin];

    IReadOnlyList<AccessMatrixData> ISectionAccessMatrix.AccessMatrices =>
    [
        new()
        {
            Key = "CityPlanningOverview",
            SectionName = "City Planning Overview",
            Order = 100,
            Roles = ["Volunteer", "Barrio Lead", "Map Admin"],
            Features =
            [
                Of("View the map", ("Volunteer", Allowed), ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Toggle layers (containers, camp limits)", ("Volunteer", Allowed), ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Measure distances", ("Volunteer", Allowed), ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Navigate to barrio placement", ("Volunteer", Denied), ("Barrio Lead", Limited), ("Map Admin", Allowed)),
                Of("Navigate to container placement", ("Volunteer", Denied), ("Barrio Lead", Limited), ("Map Admin", Allowed)),
            ]
        },
        new()
        {
            Key = "CityPlanningBarrioMap",
            SectionName = "Barrio Placement",
            Order = 110,
            Roles = ["Barrio Lead", "Map Admin"],
            Features =
            [
                Of("View barrio polygons", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Place / edit own barrio polygon (placement open)", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Edit any barrio polygon", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("View polygon history", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Restore historical polygon version", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Measure distances", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Open / close placement phase", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Configure settings (dates, zones, limit zone)", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Manage containers", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Export GeoJSON", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
            ]
        },
        new()
        {
            Key = "ContainerMap",
            SectionName = "Container Placement",
            Order = 120,
            Roles = ["Barrio Lead", "Map Admin"],
            Features =
            [
                Of("View placed containers", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Place / move own containers (placement open)", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Place / move any container", ("Barrio Lead", Denied), ("Map Admin", Allowed)),
                Of("Clear container placement", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
                Of("Measure distances", ("Barrio Lead", Allowed), ("Map Admin", Allowed)),
            ]
        }
    ];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("City Planning", [
            // The page also admits city-planning team members; they reach it from the member-side City page.
            new("Barrio map", "CityPlanning", "Admin", null, null, "fa-solid fa-map", CampsPolicies.Manage)
        ])
    ];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "CityPlanning:CityPlanningTeamSlug", "City Planning");
    }

    void ISectionEndpoints.MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<CityPlanningHub>("/hubs/city-planning");
    }

    IReadOnlyList<SectionHelpEntry> ISectionHelp.HelpEntries => Entries;

    private static readonly IReadOnlyList<SectionHelpEntry> Entries =
    [
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "ContainerMap", 100),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "CityPlanningOverview", 110),
        SectionHelpEntry.FromEmbeddedMarkdown(typeof(Section).Assembly, "CityPlanningBarrioMap", 120),
    ];

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
        [new("Nav_City", Controller: "CityPlanning", Action: "Index", Policy: PolicyNames.AppAccess, Weight: 40)];

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        // The OR lives in CityPlanningMapAdminHandler, since policy requirements AND together.
        options.AddPolicy(PolicyNames.CityPlanningMapAdmin, policy =>
            policy.AddRequirements(new CityPlanningMapAdminRequirement()));
    }

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("city-planning", "Settings_TabCityPlanning", typeof(CityPlanningSettingsTabViewComponent), PolicyNames.CityPlanningMapAdmin)
    ];
}
