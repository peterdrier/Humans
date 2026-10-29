using Humans.Rideshare.Contracts;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models.Tables;
using Humans.Gdpr.Contracts;
using Humans.Rideshare.Data;
using Humans.Rideshare.Domain;
using Humans.Rideshare.Services;
using Humans.Rideshare.Services.Routing;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Humans.Rideshare;

public sealed class Section : ISection, ISectionRoles
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(RideshareRoles.RideshareAdmin, "Rideshare Admin",
            "Road captains. They set the year's destination and travel windows, watch the season statistics, and keep the day roster of trips and riders.",
            "fa-solid fa-car"),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationSourceContributor, RideshareNotificationSources>();
        services.AddSectionDbContext<RideshareDbContext>(sentinelTable: "rideshare_trips");

        services.AddSingleton<IRideshareRepository, RideshareRepository>();

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

        services.AddKeyedScoped<IRideshareService, RideshareService>(CachingRideshareService.InnerServiceKey);
        services.AddSingleton<CachingRideshareService>();
        services.AddSingleton<IRideshareService>(sp => sp.GetRequiredService<CachingRideshareService>());

        // GDPR and account-merge fan-outs bind to the decorator, not the inner: erasure and
        // the merge fold change cached rows.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingRideshareService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CachingRideshareService>());

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
