using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Holded;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Finance;
using Humans.Infrastructure.Services.Holded;

namespace Humans.Web.Extensions.Sections;

public static class HoldedSectionExtensions
{
    public static IServiceCollection AddHoldedSection(
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

        services.AddScoped<IHoldedRepository, HoldedRepository>();
        services.AddScoped<IHoldedFinanceService, HoldedFinanceService>();
        services.AddScoped<HoldedSyncJob>();

        return services;
    }
}
