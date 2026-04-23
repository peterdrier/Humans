using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using CampsCampContactService = Humans.Application.Services.Camps.CampContactService;
using CampsCampService = Humans.Application.Services.Camps.CampService;

namespace Humans.Web.Extensions.Sections;

internal static class CampsSectionExtensions
{
    internal static IServiceCollection AddCampsSection(this IServiceCollection services)
    {
        // Camps section — §15 repository pattern (issue #542).
        services.AddSingleton<ICampRepository, CampRepository>();
        services.AddSingleton<ICampImageStorage, CampImageStorage>();
        services.AddScoped<CampsCampService>();
        services.AddScoped<ICampService>(sp => sp.GetRequiredService<CampsCampService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampsCampService>());

        services.AddScoped<ICampContactService, CampsCampContactService>();

        return services;
    }
}
