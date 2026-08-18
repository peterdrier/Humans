using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Services;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.CityPlanning;

/// <summary>
/// City Planning's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix. Replaces Shell's
/// <c>AddCityPlanningSection</c> verbatim (design §6): same lifetimes, same order.
/// </summary>
/// <remarks>
/// No caching decorator: a small admin-facing section whose reads are already a single row
/// or a per-year polygon list. Cross-section reads (camps, teams, users) route through the
/// owning service interfaces.
/// <para>
/// <c>Lazy&lt;ICityPlanningService&gt;</c> stays registered in Shell beside the Camps
/// section: it exists to break the CampService ↔ CityPlanningService construction cycle, so
/// it belongs with the consumer, not the producer.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CityPlanningOptions>(configuration.GetSection(CityPlanningOptions.SectionName));

        services.AddSectionDbContext<CityPlanningDbContext>(sentinelTable: "city_planning_settings");

        services.AddSingleton<ICityPlanningRepository, CityPlanningRepository>();
        services.AddScoped<CityPlanningService>();
        services.AddScoped<ICityPlanningService>(sp => sp.GetRequiredService<CityPlanningService>());
        services.AddScoped<ICityPlanningServiceRead>(sp => sp.GetRequiredService<CityPlanningService>());
    }
}
