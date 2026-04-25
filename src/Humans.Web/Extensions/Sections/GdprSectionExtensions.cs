using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Services.Gdpr;

namespace Humans.Web.Extensions.Sections;

internal static class GdprSectionExtensions
{
    internal static IServiceCollection AddGdprSection(this IServiceCollection services)
    {
        // GDPR export orchestrator — pure fan-out over every IUserDataContributor
        services.AddScoped<IGdprExportService, GdprExportService>();

        return services;
    }
}
