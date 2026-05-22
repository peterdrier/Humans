using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Metering;
using Humans.Infrastructure.Services;
using Humans.Infrastructure.Services.Metering;

namespace Humans.Web.Extensions.Infrastructure;

internal static class TelemetryInfrastructureExtensions
{
    internal static IServiceCollection AddTelemetryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GitHubSettings>(configuration.GetSection(GitHubSettings.SectionName));

        services.AddSingleton<IHumansMetrics, HumansMetricsService>();

        // Process-local "who's online" registry — singleton so the dict survives across requests.
        services.AddSingleton<IUserActivityTracker, UserActivityTracker>();

        // IMeters is a leaf singleton — only ILogger dep. Owns the
        // System.Diagnostics.Metrics.Meter("Humans.Metrics") instrument under which
        // every section-declared gauge is automatically exported via the existing
        // AddMeter("Humans.Metrics") subscription.
        services.AddSingleton<IMeters, MetersService>();

        // Coarse client demographics (OS/browser/device + screen resolution) for /Admin/ClientStats.
        services.AddSingleton<IClientStatsTracker, ClientStatsTracker>();

        // HTTP status-code tally via a MeterListener over the ASP.NET Core hosting meter.
        // Hosted so the listener attaches at startup and counts from the first request.
        services.AddSingleton<HttpStatusTracker>();
        services.AddSingleton<IHttpStatusTracker>(sp => sp.GetRequiredService<HttpStatusTracker>());
        services.AddHostedService(sp => sp.GetRequiredService<HttpStatusTracker>());

        return services;
    }
}
