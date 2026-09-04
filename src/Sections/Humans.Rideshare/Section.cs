using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models.Tables;
using Humans.Gdpr.Contracts;
using Humans.Rideshare.Data;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Humans.Rideshare.Services.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Rideshare;

/// <summary>
/// Rideshare's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<RideshareDbContext>(sentinelTable: "rideshare_trips");

        // Singleton + IDbContextFactory pattern (§15b): repo owns context lifetime.
        services.AddSingleton<IRideshareRepository, RideshareRepository>();

        // OpenRouteService: key from the environment, base URL from configuration.
        services.Configure<RouteProviderOptions>(o =>
        {
            o.ApiKey = Environment.GetEnvironmentVariable("ORS_API_KEY") ?? "";
            o.BaseUrl = configuration["Rideshare:RouteProvider:BaseUrl"] ?? "https://api.openrouteservice.org";
        });
        services.AddHttpClient<IRouteProvider, OpenRouteServiceClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<RouteProviderOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            // The geocode request carries the API key in its query string; the default
            // HttpClient logger prints request URLs at Information, which would leak it.
            .RemoveAllLoggers();

        // Inner service — Scoped + keyed; the Singleton decorator resolves it per call
        // and is what unkeyed IRideshareService resolves to.
        services.AddKeyedScoped<IRideshareService, RideshareService>(CachingRideshareService.InnerServiceKey);
        services.AddSingleton<CachingRideshareService>();
        services.AddSingleton<IRideshareService>(sp => sp.GetRequiredService<CachingRideshareService>());

        // GDPR fan-out binds to the decorator, not the inner: erasure empties cached rows.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingRideshareService>());

        // Surface the snapshot cache on /Debug/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingRideshareService>().SnapshotCacheStats);

        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [TripStatus.Active] = "bg-success",
            [TripStatus.Cancelled] = "bg-dark",
            [RequestStatus.Active] = "bg-success",
            [RequestStatus.Cancelled] = "bg-dark",
            [InterestStatus.Pending] = "bg-warning text-dark",
            [InterestStatus.Accepted] = "bg-success",
            [InterestStatus.Declined] = "bg-secondary",
            [InterestStatus.Withdrawn] = "bg-dark",
        });
    }
}
