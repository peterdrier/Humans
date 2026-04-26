using Humans.Application.Configuration;
using Humans.Application.Interfaces.Metering;
using Humans.Infrastructure.Services.Metering;

namespace Humans.Web.Extensions.Infrastructure;

internal static class TelemetryInfrastructureExtensions
{
    internal static IServiceCollection AddTelemetryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GitHubSettings>(configuration.GetSection(GitHubSettings.SectionName));

        // IMeters is a leaf singleton — only ILogger dep. Owns the
        // System.Diagnostics.Metrics.Meter("Humans.Metrics") instrument under which
        // every section-declared gauge is automatically exported via the existing
        // AddMeter("Humans.Metrics") subscription.
        services.AddSingleton<IMeters, MetersService>();

        return services;
    }
}
