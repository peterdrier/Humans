using Humans.Application.Interfaces.Holded;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services.Holded;

namespace Humans.Web.Extensions.Sections;

/// <summary>
/// The Holded HTTP client and the nightly pull job. **Not** the Finance section — Finance moved
/// to <c>src/Sections/Humans.Finance</c> and registers itself (nobodies-collective/Humans#866).
/// </summary>
/// <remarks>
/// <para>
/// <c>IHoldedClient</c> is an external API connector with its own <c>docs/sections/Holded.md</c>,
/// consumed by Expenses as well as Finance. It stays in Base exactly as <c>IStripeService</c> did
/// through Store's move; it owns no tables and is not a vertical.
/// </para>
/// <para>
/// <c>HoldedSyncJob</c> stays too, for a different reason: recurring jobs are named by concrete
/// type in Shell's <c>UseHumansRecurringJobs</c> roll-call and there is no <c>ISection</c>-style
/// discovery seam for them, so a job that moved into a section would have to be public to be
/// scheduled. It reaches Finance through <c>Humans.Finance.Contracts</c> like any other Base
/// consumer. Recorded on nobodies-collective/Humans#866 as the first §15 gap of its kind.
/// </para>
/// </remarks>
public static class HoldedConnectorExtensions
{
    public static IServiceCollection AddHoldedConnector(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<HoldedClientOptions>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("HOLDED_API_KEY") ?? "";
            opts.BaseUrl = config["Holded:BaseUrl"] ?? "https://api.holded.com";
        });

        services.AddHttpClient<IHoldedClient, HoldedClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<HoldedClientOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<HoldedSyncJob>();

        return services;
    }
}
