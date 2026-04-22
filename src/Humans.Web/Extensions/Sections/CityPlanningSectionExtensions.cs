using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories;
using CityPlanningCityPlanningService = Humans.Application.Services.CityPlanning.CityPlanningService;

namespace Humans.Web.Extensions.Sections;

internal static class CityPlanningSectionExtensions
{
    internal static IServiceCollection AddCityPlanningSection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<CityPlanningOptions>(configuration.GetSection(CityPlanningOptions.SectionName));

        // City Planning section — repository + Application-layer service (§15 migration, PR #543).
        // Small admin-facing section, no caching decorator warranted. Cross-section reads
        // (camps, teams, profiles, users) route through the owning service interfaces.
        services.AddSingleton<ICityPlanningRepository, CityPlanningRepository>();
        services.AddScoped<ICityPlanningService, CityPlanningCityPlanningService>();

        return services;
    }
}
