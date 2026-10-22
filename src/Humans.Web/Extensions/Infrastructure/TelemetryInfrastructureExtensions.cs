using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Metering;
using Humans.Web.Services;
using Humans.Base.Services.Metering;

namespace Humans.Web.Extensions.Infrastructure;

internal static class TelemetryInfrastructureExtensions
{
    internal static IServiceCollection AddTelemetryInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<GitHubSettings>(configuration.GetSection(GitHubSettings.SectionName));

        services.AddSingleton<IHumansMetrics, HumansMetricsService>();
        // Hosted so the gauge-refresh timer is armed in StartAsync, after DatabaseMigrationHostedService has applied
        // pending migrations; arming it earlier raced the schema on deploy.
        services.AddHostedService(sp => (HumansMetricsService)sp.GetRequiredService<IHumansMetrics>());

        // Process-local "who's online" registry — singleton so the dict survives across requests.
        services.AddSingleton<IUserActivityTracker, UserActivityTracker>();

        // Owns the "Humans.Metrics" meter, so every section-declared gauge is exported through the existing
        // AddMeter subscription.
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
