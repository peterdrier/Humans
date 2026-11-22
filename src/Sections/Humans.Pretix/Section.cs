using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Humans.Pretix.Services;
using Humans.Tickets.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Pretix;

/// <summary>
/// The pretix checkout backend. Bound in every environment, live whenever configured: pretix runs a test mode of its
/// own, and QA needs a real instance to try the option against. Unconfigured, <c>IsConfigured</c> is false and
/// Tickets opens no checkout on it.
/// </summary>
public sealed class Section : ISection, ISectionConfiguration, ISectionAdminNav
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PretixSettings>(opts =>
        {
            configuration.GetSection(PretixSettings.SectionName).Bind(opts);
            opts.ApiToken = Environment.GetEnvironmentVariable("PRETIX_API_TOKEN") ?? opts.ApiToken;
            opts.WebhookSecret = Environment.GetEnvironmentVariable("PRETIX_WEBHOOK_SECRET") ?? opts.WebhookSecret;
            opts.StripeKey = Environment.GetEnvironmentVariable(PretixCheckoutBackend.StripeKeyVariable) ?? string.Empty;
        });

        services.AddHttpClient<PretixCheckoutBackend>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<ITicketCheckoutBackend>(sp => sp.GetRequiredService<PretixCheckoutBackend>());
    }

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Pretix:BaseUrl", "Pretix");
        configuration.GetOptionalSetting(registry, "Pretix:Organizer", "Pretix");
        registry.RegisterEnvironmentVariable("PRETIX_API_TOKEN", "Pretix", isSensitive: true);
        registry.RegisterEnvironmentVariable("PRETIX_WEBHOOK_SECRET", "Pretix", isSensitive: true);
        registry.RegisterEnvironmentVariable(PretixCheckoutBackend.StripeKeyVariable, "Pretix", isSensitive: true);
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
        [TicketsBackendAdminNav.Group("pretix", PretixCheckoutBackend.BackendKey, "fa-solid fa-store")];
}
