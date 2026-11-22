using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.HiEvents.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.HiEvents;

/// <summary>
/// Registers the Hi.Events ticketing platform whatever the environment: it does nothing until <c>HiEvents:ApiUrl</c> and
/// <c>HIEVENTS_API_KEY</c> are set, and Tickets takes no orders on it while <c>IsConfigured</c> is false.
/// </summary>
public sealed class Section : ISection, ISectionConfiguration, ISectionAdminNav
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HiEventsSettings>(opts =>
        {
            configuration.GetSection(HiEventsSettings.SectionName).Bind(opts);
            opts.ApiKey = Environment.GetEnvironmentVariable("HIEVENTS_API_KEY") ?? string.Empty;
            opts.WebhookSecret = Environment.GetEnvironmentVariable("HIEVENTS_WEBHOOK_SECRET") ?? string.Empty;
        });
        services.AddHttpClient<HiEventsPlatform>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<ITicketingPlatform>(sp => sp.GetRequiredService<HiEventsPlatform>());
    }

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "HiEvents:ApiUrl", "Hi.Events");
        configuration.GetOptionalSetting(registry, "HiEvents:AppUrl", "Hi.Events");
        registry.RegisterEnvironmentVariable("HIEVENTS_API_KEY", "Hi.Events", isSensitive: true);
        registry.RegisterEnvironmentVariable("HIEVENTS_WEBHOOK_SECRET", "Hi.Events", isSensitive: true);
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
        [TicketsPlatformAdminNav.Group("Hi.Events", HiEventsPlatform.PlatformKey, "fa-solid fa-calendar-check")];
}
