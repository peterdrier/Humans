using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Infrastructure;

internal static class TelemetryInfrastructureExtensions
{
    internal static IServiceCollection AddTelemetryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GitHubSettings>(configuration.GetSection(GitHubSettings.SectionName));

        services.AddSingleton<IHumansMetrics, HumansMetricsService>();

        return services;
    }
}
