using Humans.Camps.Contracts;
using Humans.Base.Constants;
using Humans.CityPlanning.Authorization;
using Humans.Issues.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Services;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.CityPlanning;

/// <summary>
/// <c>Lazy&lt;ICityPlanningWrite&gt;</c> is registered by Camps' <c>Section</c>, not here: it breaks the
/// CampService ↔ CityPlanningService construction cycle, so it belongs with the consumer.
/// </summary>
public sealed class Section : ISection, IIssueQueueOwner
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, CityPlanningAuditActions>();
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
}
