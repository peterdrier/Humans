using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.HiEvents.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.HiEvents;

/// <summary>
/// Registers the Hi.Events checkout backend whatever the environment: it does nothing until <c>HiEvents:ApiUrl</c> and
/// <c>HIEVENTS_API_KEY</c> are set, and Tickets refuses to open checkouts on it while <c>IsConfigured</c> is false.
/// </summary>
public sealed class Section : ISection, ISectionConfiguration
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HiEventsSettings>(opts =>
        {
            configuration.GetSection(HiEventsSettings.SectionName).Bind(opts);
            opts.ApiKey = Environment.GetEnvironmentVariable("HIEVENTS_API_KEY") ?? string.Empty;
            opts.WebhookSecret = Environment.GetEnvironmentVariable("HIEVENTS_WEBHOOK_SECRET") ?? string.Empty;
        });
        services.AddHttpClient<HiEventsCheckoutBackend>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<ITicketCheckoutBackend>(sp => sp.GetRequiredService<HiEventsCheckoutBackend>());
    }

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "HiEvents:ApiUrl", "Hi.Events");
        configuration.GetOptionalSetting(registry, "HiEvents:AppUrl", "Hi.Events");
        foreach (var hiEvent in configuration.GetSection("HiEvents:Events").GetChildren())
        {
            configuration.GetOptionalSetting(registry, $"{hiEvent.Path}:TicketProductId", "Hi.Events");
            configuration.GetOptionalSetting(registry, $"{hiEvent.Path}:DonationProductId", "Hi.Events");
        }
        registry.RegisterEnvironmentVariable("HIEVENTS_API_KEY", "Hi.Events", isSensitive: true);
        registry.RegisterEnvironmentVariable("HIEVENTS_WEBHOOK_SECRET", "Hi.Events", isSensitive: true);
    }
}
